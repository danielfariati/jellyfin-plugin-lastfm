var LastfmScrobblerConfigurationPage = {
    _pluginUniqueId: "5e7fe7f0-b048-429e-a431-b1a7e69c930d",
    _users: [],
    _config: [],

    _currentUserId: '',
    _currentUserConfig: null,

    configDefaults: {
        Username: '',
        SessionKey: '',
        MediaBrowserUserId: '',
        LastfmApiHost: 'ws.audioscrobbler.com',
        Options: {
            Scrobble: false,
            SyncFavourites: false,
            AlternativeMode: false
        }
    },

    $el: null,

    init: function ($ctx) {
        Dashboard.showLoadingMsg();

        this.$el = $ctx;

        this.$el.find('#LastfmScrobblerConfigurationForm')
            .off('submit.lastfm')
            .on('submit.lastfm', this, this.onSubmit);
        this.$el.find('#lastfmUser')
            .off('change.lastfm')
            .on('change.lastfm', this, $.proxy(this.onUserChange, this));

        var loadConfig = this.loadConfiguration();
        var loadUsers = this.loadUsers().then($.proxy(this.buildUserList, this));

        // When we load everything, set the current user
        $.when(loadConfig, loadUsers).then($.proxy(function () {
            // Populate the inputs with the default selected user
            this.onUserChange();

            Dashboard.hideLoadingMsg();
        }, this)).then(null, $.proxy(function (error) {
            this.handleError('Unable to load the Last.fm configuration or Jellyfin user list.', error);
        }, this));
    },

    handleError: function (message, error) {
        Dashboard.hideLoadingMsg();
        console.error(message, error);

        var detail = error && (error.message || error.statusText);
        Dashboard.alert(detail ? message + ' ' + detail : message + ' Check the browser console for details.');
    },

    loadConfiguration: function () {
        return ApiClient.getPluginConfiguration(this._pluginUniqueId).then($.proxy(function (config) {
            this._config = config;
        }, this));
    },

    loadUsers: function () {
        return ApiClient.getUsers().then($.proxy(function (users) {
            this._users = users;
        }, this));
    },

    saveConfiguration: function () {
        return ApiClient.updatePluginConfiguration(this._pluginUniqueId, this._config);
    },

    buildUserList: function () {
        var select = this.$el.find('#lastfmUser').empty().get(0);

        $.each(this._users, function (i, user) {
            var option = document.createElement('option');
            option.value = user.Id;
            option.textContent = user.Name;
            select.appendChild(option);
        });
    },

    populateInputs: function (userData) {
        var data = $.extend({}, this.configDefaults, userData);

        this.$el.find('#lastfmApiHost').val(this._config.LastfmApiHost || this.configDefaults.LastfmApiHost);
        this.$el.find('#lastfmUsername').val(data.Username);
        this.$el.find('#lastfmPassword').val(data.SessionKey);
        this.$el.find('#lastfmOptionScrobble').prop('checked', data.Options.Scrobble);
        this.$el.find('#lastfmOptionFavourite').prop('checked', data.Options.SyncFavourites);
        this.$el.find('#lastfmOptionAltMode').prop('checked', data.Options.AlternativeMode);
    },

    save: function (username, password) {
        var userConfig = this.getCurrentSelectedUser();
        var previousApiHost = this._config.LastfmApiHost || this.configDefaults.LastfmApiHost;
        var newApiHost = this.$el.find('#lastfmApiHost').val() || this.configDefaults.LastfmApiHost;
        var apiHostChanged = (previousApiHost || '').trim().toLowerCase() !== (newApiHost || '').trim().toLowerCase();

        this._config.LastfmApiHost = newApiHost;

        if (!username && !password) {
            return this.doSave();
        }

        // If the config for the user doesn't exist, create one
        if (!userConfig) {
            userConfig = $.extend({}, this.configDefaults, { MediaBrowserUserId: this.getCurrentSelectedUserId() });

            this._config.LastfmUsers.push(userConfig);
        }

        // Attach current option settings to
        userConfig.Options = this.getUIOptionsValues();

        // Dont try and login again if the password field is still the SessionKey
        if (userConfig.SessionKey == password && !apiHostChanged) {
            return this.doSave();
        }

        // Get session with user data
        return ApiClient.LastfmGetMobileSession(username, password, this._config.LastfmApiHost).then($.proxy(function (data) {
            // Check if we have data
            if (data && data.session) {

                userConfig.Username = data.session.name;
                userConfig.SessionKey = data.session.key;

                // Save
                return this.doSave();
            }

            var error = new Error(data && data.message ? data.message : 'Last.fm did not return a session.');
            this.handleError('Unable to authenticate with Last.fm.', error);
            return null;
        }, this), $.proxy(function (error) {
            this.handleError('Unable to authenticate with Last.fm.', error);
            return null;
        }, this));
    },

    doSave: function () {
        return this.saveConfiguration().then($.proxy(function (result) {
            Dashboard.processPluginConfigurationUpdateResult(result);

            this.onUserChange();
        }, this));
    },

    getCurrentSelectedUserId: function () {
        return this.$el.find('#lastfmUser').val();
    },

    getCurrentSelectedUser: function () {
        // Get the current user
        var currentUserId = this.getCurrentSelectedUserId();

        var currentUser = this._config.LastfmUsers.filter(function (user) {
            return user.MediaBrowserUserId == currentUserId;
        })[0];

        return currentUser;
    },

    getSelectedMBUser: function () {
        // Get the current user
        var currentUserId = this.getCurrentSelectedUserId();

        var currentUser = this._users.filter(function (user) {
            return user.Id == currentUserId;
        })[0];

        return currentUser;
    },

    getUIOptionsValues: function () {
        var options = $.extend({}, this.configDefaults.Options);

        options.Scrobble = this.$el.find('#lastfmOptionScrobble').prop('checked');
        options.SyncFavourites = this.$el.find('#lastfmOptionFavourite').prop('checked');
        options.AlternativeMode = this.$el.find('#lastfmOptionAltMode').prop('checked');
        return options;
    },

    onUserChange: function () {
        var currentUser = this.getCurrentSelectedUser();

        this.populateInputs(currentUser);
    },

    onSubmit: function (e) {
        e.preventDefault();

        var self = e.data;

        var username = $(this).find("#lastfmUsername").val();
        var password = $(this).find("#lastfmPassword").val();

        Dashboard.showLoadingMsg();

        // Load the config again in case another user has updated their Lastfm info
        self.loadConfiguration().then(function () {
            return self.save(username, password);
        }).then(null, function (error) {
            self.handleError('Unable to save the Last.fm configuration.', error);
        });

        return false;
    },
};

// Keep the controller available for diagnostics in the browser console.
window.LastfmScrobblerConfigurationPage = LastfmScrobblerConfigurationPage;

// Add my own methods to the api client
ApiClient.LastfmGetMobileSession = function (username, password, apiHost) {
    if (!username) throw new Error("no username");

    if (!password) throw new Error("no password");

    var url = this.getUrl("Lastfm/Login");

    var data = {
        username: username,
        password: password,
        apiHost: apiHost
    };

    return this.ajax({
        type: "POST",
        url: url,
        data: JSON.stringify(data),
        contentType: "application/json",
        dataType: 'json'
    });
};

export default function (view) {
    $(view).off('viewshow.lastfm').on('viewshow.lastfm', function () {
        LastfmScrobblerConfigurationPage.init($(view));
    });
}
