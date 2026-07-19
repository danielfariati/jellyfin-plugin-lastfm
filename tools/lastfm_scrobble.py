#!/usr/bin/env python3
"""
Replay a scrobble against Last.fm by hand, outside the plugin.

This uses the same API key, secret and signing scheme the plugin uses, so if Last.fm
rejects a scrobble here as well, the plugin is not the cause. It is meant to be run on
the Jellyfin server itself, since it reads the session key straight from the plugin
configuration - nothing has to be pasted in, and no credential is ever printed.

The easiest way to use it is to copy the payload= value out of a plugin log line and
hand it straight back:

  [WRN] ... Scrobble ignored by Last.fm: user="me", code=1, reason="Artist was ignored",
        payload="artist=The%20Black%20Keys&method=track.scrobble&timestamp=1784442656&track=Strange%20Times"

  python3 lastfm_scrobble.py \
      --config /var/lib/jellyfin/plugins/configurations/Jellyfin.Plugin.Lastfm.xml \
      --payload "artist=The%20Black%20Keys&method=track.scrobble&timestamp=1784442656&track=Strange%20Times" \
      --dry-run

Drop --dry-run and pass --send once the preview looks right. --now replaces the original
timestamp with the current time, which may matter because Last.fm ignores scrobbles that are
more than about two weeks old (ignore code 3) - without it, replaying an old log line can
fail for a reason that has nothing to do with the original problem.

Common paths for --config:
  Linux            /var/lib/jellyfin/plugins/configurations/Jellyfin.Plugin.Lastfm.xml
  Docker           /config/plugins/configurations/Jellyfin.Plugin.Lastfm.xml
  Windows          C:\\ProgramData\\Jellyfin\\Server\\plugins\\configurations\\Jellyfin.Plugin.Lastfm.xml
  macOS            ~/.local/share/jellyfin/plugins/configurations/Jellyfin.Plugin.Lastfm.xml
"""
import argparse
import hashlib
import json
import os
import re
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import xml.etree.ElementTree as ET

# Credentials live in the plugin source, next to this script in the repository.
REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
STRINGS = os.path.join(REPO_ROOT, "Jellyfin.Plugin.Lastfm", "Resources", "Strings.cs")

DEFAULT_ENDPOINT = "https://ws.audioscrobbler.com/2.0/"
CREDENTIAL_KEYS = ("api_key", "sk", "api_sig")

def load_api_credentials(api_key, api_secret):
    if api_key and api_secret:
        return api_key, api_secret
    if not os.path.exists(STRINGS):
        sys.exit(
            f"Could not find {STRINGS}.\n"
            "Run this from a checkout of the plugin repository, or pass --api-key and --api-secret."
        )
    src = open(STRINGS, encoding="utf-8-sig").read()
    key = re.search(r'LastfmApiKey\s*=\s*"([^"]+)"', src)
    secret = re.search(r'LastfmApiSecret\s*=\s*"([^"]+)"', src)
    if not key or not secret:
        sys.exit(f"Could not read the API key/secret from {STRINGS}. Pass --api-key and --api-secret instead.")
    return api_key or key.group(1), api_secret or secret.group(1)


def read_users(config_path):
    """Every configured entry, as (lastfm_username, jellyfin_user_id, session_key, options)."""
    if not os.path.exists(config_path):
        sys.exit(f"No such file: {config_path}\nPass the path to Jellyfin.Plugin.Lastfm.xml with --config.")
    try:
        root = ET.parse(config_path).getroot()
    except ET.ParseError as e:
        sys.exit(f"Could not parse {config_path}: {e}")

    users = []
    for u in root.findall(".//LastfmUser"):
        opts = u.find("Options")
        users.append((
            (u.findtext("Username") or "").strip(),
            (u.findtext("MediaBrowserUserId") or "").strip(),
            (u.findtext("SessionKey") or "").strip(),
            {
                "scrobble": (opts.findtext("Scrobble") if opts is not None else "") or "?",
                "alternative_mode": (opts.findtext("AlternativeMode") if opts is not None else "") or "?",
            },
        ))
    if not users:
        sys.exit(f"No Last.fm users are configured in {config_path}.")
    return users


def describe_users(users):
    lines = []
    for name, jf_id, session, opts in users:
        lines.append(
            f"  Last.fm user : {name or '(not authenticated)'}\n"
            f"  Jellyfin id  : {jf_id or '(unknown)'}\n"
            f"  Session key  : {'present' if session else 'MISSING - reconfigure the plugin'}\n"
            f"  Scrobbling   : {opts['scrobble']}   Alternative mode: {opts['alternative_mode']}"
        )
    return "\n\n".join(lines)


def load_session(config_path, username):
    users = read_users(config_path)

    if username:
        # matches either the Last.fm username or the Jellyfin user id, so this works whichever of the two you happen to know
        wanted = username.strip().lower()
        for name, jf_id, session, _ in users:
            if wanted in (name.lower(), jf_id.lower()):
                if not session:
                    sys.exit(f"{name or jf_id!r} has no session key. Re-enter the credentials in the plugin settings.")
                return name or jf_id, session
        sys.exit(f"No configured user matches {username!r}. Configured:\n\n" + describe_users(users))

    if len(users) > 1:
        sys.exit("Several users are configured. Pick one with --user, by Last.fm username "
                 "or Jellyfin user id:\n\n" + describe_users(users))

    name, jf_id, session, _ = users[0]
    if not session:
        sys.exit(f"{name or jf_id!r} has no session key. Re-enter the credentials in the plugin settings.")
    return name or jf_id, session


def parse_payload(payload):
    """Accepts a payload exactly as logged by the plugin (url encoded, & separated)."""
    parsed = urllib.parse.parse_qs(payload, keep_blank_values=False, strict_parsing=True)
    params = {k: v[0] for k, v in parsed.items()}
    for k in CREDENTIAL_KEYS + ("format",):
        params.pop(k, None)
    return params


def sign(params, secret):
    """Same as Helpers.CreateSignature: sort by key, concatenate key+value, append secret, md5."""
    joined = "".join(f"{k}{v}" for k, v in sorted(params.items()))
    return hashlib.md5((joined + secret).encode("utf-8")).hexdigest()


def redact(params):
    return {k: ("<redacted>" if k in CREDENTIAL_KEYS else v) for k, v in params.items()}


def call(params, secret, endpoint, send):
    params = dict(params)
    params["api_sig"] = sign(params, secret)
    params["format"] = "json"
    if not send:
        print(f"Would POST to {endpoint}")
        print(json.dumps(redact(params), indent=2, sort_keys=True))
        return None
    req = urllib.request.Request(endpoint, data=urllib.parse.urlencode(params).encode("utf-8"), method="POST")
    try:
        with urllib.request.urlopen(req) as r:
            return r.status, json.loads(r.read().decode("utf-8"))
    except urllib.error.HTTPError as e:
        return e.code, json.loads(e.read().decode("utf-8"))


def report(status, data):
    print(f"HTTP {status}")
    print(json.dumps(data, indent=2))
    attr = (data.get("scrobbles") or {}).get("@attr") or {}
    scrobble = (data.get("scrobbles") or {}).get("scrobble") or {}
    ignored = scrobble.get("ignoredMessage") or {}
    print("\n--- verdict ---")
    if data.get("error"):
        print(f"API ERROR {data['error']}: {data.get('message')}")
    elif int(attr.get("ignored", 0) or 0) > 0:
        print(f"IGNORED by Last.fm. code={ignored.get('code')} text={ignored.get('#text')!r}")
        print("The plugin was not involved here, so this is not a plugin bug.")
    else:
        print(f"ACCEPTED (accepted={attr.get('accepted')}). Check the Last.fm profile to confirm.")


def main():
    ap = argparse.ArgumentParser(
        description="Replay a Last.fm scrobble outside the plugin, to tell plugin problems apart from Last.fm ones.",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog=__doc__,
    )
    ap.add_argument("--config", required=True, metavar="PATH",
                    help="Path to Jellyfin.Plugin.Lastfm.xml (required)")
    ap.add_argument("--user", metavar="NAME_OR_ID",
                    help="Which configured user to send as: either the Last.fm username or the "
                         "Jellyfin user id. Required when more than one user is configured")
    ap.add_argument("--list", action="store_true",
                    help="List the users configured in the plugin and exit")
    ap.add_argument("--payload", metavar="STR",
                    help='The payload= value copied from a plugin log line')
    ap.add_argument("--artist")
    ap.add_argument("--track")
    ap.add_argument("--album")
    ap.add_argument("--album-artist", dest="album_artist")
    ap.add_argument("--mbid")
    ap.add_argument("--timestamp", type=int, help="Unix time the track started playing")
    ap.add_argument("--now", action="store_true",
                    help="Use the current time instead of the timestamp in the payload "
                         "(Last.fm ignores scrobbles older than about two weeks)")
    ap.add_argument("--endpoint", default=DEFAULT_ENDPOINT,
                    help=f"API endpoint, for Last.fm compatible services (default: {DEFAULT_ENDPOINT})")
    ap.add_argument("--api-key", help="Override the API key instead of reading it from the plugin source")
    ap.add_argument("--api-secret", help="Override the shared secret")
    ap.add_argument("--send", action="store_true", help="Actually submit the scrobble")
    ap.add_argument("--dry-run", action="store_true", help="Show the signed request without sending it")
    ap.add_argument("--whoami", action="store_true", help="Check the session key only, submit nothing")
    args = ap.parse_args()

    if args.list:
        print(describe_users(read_users(args.config)))
        return

    api_key, api_secret = load_api_credentials(args.api_key, args.api_secret)
    username, session_key = load_session(args.config, args.user)
    print(f"Using account: {username}  (session key loaded from the config, not shown)\n")

    if args.whoami:
        result = call({"method": "user.getInfo", "api_key": api_key, "sk": session_key},
                      api_secret, args.endpoint, send=True)
        status, data = result
        print(f"HTTP {status}")
        if data.get("error"):
            print(f"API ERROR {data['error']}: {data.get('message')}")
        else:
            user = data.get("user", {})
            print(f"Session key is valid for {user.get('name')!r} "
                  f"({user.get('playcount')} scrobbles).")
        return

    if args.payload:
        params = parse_payload(args.payload)
    else:
        if not args.artist or not args.track:
            sys.exit("Pass --payload, or at least --artist and --track. See --help.")
        params = {"method": "track.scrobble", "artist": args.artist, "track": args.track}
        if args.album:
            params["album"] = args.album
        if args.mbid:
            params["mbid"] = args.mbid
        # the plugin only sends albumArtist when it differs from artist
        if args.album_artist and args.album_artist != args.artist:
            params["albumArtist"] = args.album_artist

    params.setdefault("method", "track.scrobble")
    if args.timestamp:
        params["timestamp"] = str(args.timestamp)
    if args.now or "timestamp" not in params:
        params["timestamp"] = str(int(time.time()) - 190)

    params["api_key"] = api_key
    params["sk"] = session_key

    if not args.send and not args.dry_run:
        sys.exit("Pass --dry-run to preview the request, or --send to actually submit it.")

    print("Sending exactly:")
    for k, v in sorted(redact(params).items()):
        print(f"  {k} = {v!r}")
    print()

    result = call(params, api_secret, args.endpoint, send=args.send)
    if result is not None:
        report(*result)


if __name__ == "__main__":
    main()
