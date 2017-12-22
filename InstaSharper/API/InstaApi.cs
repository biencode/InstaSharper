using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using InstaSharper.Classes;
using InstaSharper.Classes.Android.DeviceInfo;
using InstaSharper.Classes.Models;
using InstaSharper.Classes.ResponseWrappers;
using InstaSharper.Classes.ResponseWrappers.BaseResponse;
using InstaSharper.Converters;
using InstaSharper.Converters.Json;
using InstaSharper.Helpers;
using InstaSharper.Logger;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using InstaRecentActivityConverter = InstaSharper.Converters.Json.InstaRecentActivityConverter;

namespace InstaSharper.API
{
    internal class InstaApi : IInstaApi
    {
        private readonly IHttpRequestProcessor _httpRequestProcessor;
        private readonly IInstaLogger _logger;
        private AndroidDevice _deviceInfo;
        private UserSessionData _user;

        public InstaApi(UserSessionData user, IInstaLogger logger, AndroidDevice deviceInfo,
            IHttpRequestProcessor httpRequestProcessor)
        {
            _user = user;
            _logger = logger;
            _deviceInfo = deviceInfo;
            _httpRequestProcessor = httpRequestProcessor;
        }

        public bool IsUserAuthenticated { get; private set; }

        #region async part

        public async Task<IResult<bool>> LoginAsync()
        {
            ValidateUser();
            ValidateRequestMessage();
            try
            {
                var csrftoken = string.Empty;
                var firstResponse = await _httpRequestProcessor.GetAsync(_httpRequestProcessor.Client.BaseAddress);
                var cookies =
                    _httpRequestProcessor.HttpHandler.CookieContainer.GetCookies(_httpRequestProcessor.Client
                        .BaseAddress);
                _logger?.LogResponse(firstResponse);
                foreach (Cookie cookie in cookies)
                    if (cookie.Name == InstaApiConstants.CSRFTOKEN) csrftoken = cookie.Value;
                _user.CsrfToken = csrftoken;
                var instaUri = UriCreator.GetLoginUri();
                var signature =
                    $"{_httpRequestProcessor.RequestMessage.GenerateSignature()}.{_httpRequestProcessor.RequestMessage.GetMessageString()}";
                var fields = new Dictionary<string, string>
                {
                    {InstaApiConstants.HEADER_IG_SIGNATURE, signature},
                    {InstaApiConstants.HEADER_IG_SIGNATURE_KEY_VERSION, InstaApiConstants.IG_SIGNATURE_KEY_VERSION}
                };
                var request = HttpHelper.GetDefaultRequest(HttpMethod.Post, instaUri, _deviceInfo);
                request.Content = new FormUrlEncodedContent(fields);
                request.Properties.Add(InstaApiConstants.HEADER_IG_SIGNATURE, signature);
                request.Properties.Add(InstaApiConstants.HEADER_IG_SIGNATURE_KEY_VERSION,
                    InstaApiConstants.IG_SIGNATURE_KEY_VERSION);
                var response = await _httpRequestProcessor.SendAsync(request);
                var json = await response.Content.ReadAsStringAsync();
                if (response.StatusCode != HttpStatusCode.OK) return Result.UnExpectedResponse<bool>(response, json);
                var loginInfo =
                    JsonConvert.DeserializeObject<InstaLoginResponse>(json);
                IsUserAuthenticated = loginInfo.User != null && loginInfo.User.UserName == _user.UserName;
                var converter = ConvertersFabric.GetUserShortConverter(loginInfo.User);
                _user.LoggedInUder = converter.Convert();
                _user.RankToken = $"{_user.LoggedInUder.Pk}_{_httpRequestProcessor.RequestMessage.phone_id}";
                return Result.Success(true);
            }
            catch (Exception exception)
            {
                LogException(exception);
                return Result.Fail(exception.Message, false);
            }
        }

        public async Task<IResult<bool>> LogoutAsync()
        {
            ValidateUser();
            ValidateLoggedIn();
            try
            {
                var instaUri = UriCreator.GetLogoutUri();
                var request = HttpHelper.GetDefaultRequest(HttpMethod.Get, instaUri, _deviceInfo);
                var response = await _httpRequestProcessor.SendAsync(request);
                var json = await response.Content.ReadAsStringAsync();
                if (response.StatusCode != HttpStatusCode.OK) return Result.UnExpectedResponse<bool>(response, json);
                var logoutInfo = JsonConvert.DeserializeObject<BaseStatusResponse>(json);
                IsUserAuthenticated = logoutInfo.Status == "ok";
                return Result.Success(true);
            }
            catch (Exception exception)
            {
                LogException(exception);
                return Result.Fail(exception.Message, false);
            }
        }

        public async Task<IResult<InstaFeed>> GetUserTimelineFeedAsync(int maxPages = 0)
        {
            ValidateUser();
            ValidateLoggedIn();
            var userFeedUri = UriCreator.GetUserFeedUri();
            var request = HttpHelper.GetDefaultRequest(HttpMethod.Get, userFeedUri, _deviceInfo);
            var response = await _httpRequestProcessor.SendAsync(request);
            var json = await response.Content.ReadAsStringAsync();
            if (response.StatusCode != HttpStatusCode.OK) return Result.UnExpectedResponse<InstaFeed>(response, json);
            var feedResponse = JsonConvert.DeserializeObject<InstaFeedResponse>(json,
                new InstaFeedResponseDataConverter());
            var converter = ConvertersFabric.GetFeedConverter(feedResponse);
            var feed = converter.Convert();
            var nextId = feedResponse.NextMaxId;
            var moreAvailable = feedResponse.MoreAvailable;
            while (moreAvailable && feed.Medias.Pages < maxPages)
            {
                if (string.IsNullOrEmpty(nextId)) break;
                var nextFeed = await GetUserFeedWithMaxIdAsync(nextId);
                if (!nextFeed.Succeeded) Result.Success($"Not all pages was downloaded: {nextFeed.Info.Message}", feed);
                nextId = nextFeed.Value.NextMaxId;
                moreAvailable = nextFeed.Value.MoreAvailable;
                feed.Medias.AddRange(
                    nextFeed.Value.Items.Select(ConvertersFabric.GetSingleMediaConverter)
                        .Select(conv => conv.Convert()));
                feed.Medias.Pages++;
            }
            return Result.Success(feed);
        }

        public async Task<IResult<InsteReelFeed>> GetUserStoryFeedAsync(long userId)
        {
            ValidateUser();
            ValidateLoggedIn();
            try
            {
                var userFeedUri = UriCreator.GetUserReelFeedUri(userId);
                var request = HttpHelper.GetDefaultRequest(HttpMethod.Get, userFeedUri, _deviceInfo);
                var response = await _httpRequestProcessor.SendAsync(request);
                var json = await response.Content.ReadAsStringAsync();
                if (response.StatusCode != HttpStatusCode.OK)
                    return Result.UnExpectedResponse<InsteReelFeed>(response, json);
                var feedResponse = JsonConvert.DeserializeObject<InsteReelFeedResponse>(json);
                var feed = ConvertersFabric.GetReelFeedConverter(feedResponse).Convert();
                return Result.Success(feed);
            }
            catch (Exception exception)
            {
                return Result.Fail(exception.Message, (InsteReelFeed)null);
            }
        }

        /// <inheritdoc />
        public Stream GetStateDataAsStream()
        {
            var state = new StateData
            {
                DeviceInfo = _deviceInfo,
                IsAuthenticated = IsUserAuthenticated,
                UserSession = _user,
                Cookies = _httpRequestProcessor.HttpHandler.CookieContainer
            };
            return SerializationHelper.SerializeToStream(state);
        }

        /// <inheritdoc />
        public void LoadStateDataFromStream(Stream stream)
        {
            var data = SerializationHelper.DeserializeFromStream<StateData>(stream);
            _deviceInfo = data.DeviceInfo;
            _user = data.UserSession;
            IsUserAuthenticated = data.IsAuthenticated;
            _httpRequestProcessor.HttpHandler.CookieContainer = data.Cookies;
        }

        public async Task<IResult<InstaExploreFeed>> GetExploreFeedAsync(int maxPages = 0)
        {
            ValidateUser();
            ValidateLoggedIn();
            try
            {
                if (maxPages == 0) maxPages = int.MaxValue;
                var exploreUri = UriCreator.GetExploreUri();
                var request = HttpHelper.GetDefaultRequest(HttpMethod.Get, exploreUri, _deviceInfo);
                var response = await _httpRequestProcessor.SendAsync(request);
                var json = await response.Content.ReadAsStringAsync();
                if (response.StatusCode != HttpStatusCode.OK) return Result.Fail("", (InstaExploreFeed)null);
                var feedResponse = JsonConvert.DeserializeObject<InstaExploreFeedResponse>(json,
                    new InstaExploreFeedDataConverter());
                var exploreFeed = ConvertersFabric.GetExploreFeedConverter(feedResponse).Convert();
                var nextId = feedResponse.Items.Medias.LastOrDefault(media => !string.IsNullOrEmpty(media.NextMaxId))
                    ?.NextMaxId;
                while (!string.IsNullOrEmpty(nextId) && exploreFeed.Medias.Pages < maxPages)
                {
                    var nextFeed = await GetExploreFeedAsync(nextId);
                    if (!nextFeed.Succeeded)
                        Result.Success($"Not all pages were downloaded: {nextFeed.Info.Message}", exploreFeed);
                    nextId = feedResponse.Items.Medias.LastOrDefault(media => !string.IsNullOrEmpty(media.NextMaxId))
                        ?.NextMaxId;
                    exploreFeed.Medias.AddRange(
                        nextFeed.Value.Items.Medias.Select(ConvertersFabric.GetSingleMediaConverter)
                            .Select(conv => conv.Convert()));
                    exploreFeed.Medias.Pages++;
                }
                return Result.Success(exploreFeed);
            }
            catch (Exception exception)
            {
                LogException(exception);
                return Result.Fail(exception.Message, (InstaExploreFeed)null);
            }
        }

        public async Task<IResult<InstaMediaList>> GetUserMediaAsync(string username, int maxPages = 0)
        {
            ValidateUser();
            if (maxPages == 0) maxPages = int.MaxValue;
            var user = await GetUserAsync(username);
            if (!user.Succeeded) return Result.Fail<InstaMediaList>("Unable to get current user");
            var instaUri = UriCreator.GetUserMediaListUri(user.Value.Pk);
            var request = HttpHelper.GetDefaultRequest(HttpMethod.Get, instaUri, _deviceInfo);
            var response = await _httpRequestProcessor.SendAsync(request);
            var json = await response.Content.ReadAsStringAsync();
            if (response.StatusCode == HttpStatusCode.OK)
            {
                var mediaResponse = JsonConvert.DeserializeObject<InstaMediaListResponse>(json,
                    new InstaMediaListDataConverter());
                var moreAvailable = mediaResponse.MoreAvailable;
                var converter = ConvertersFabric.GetMediaListConverter(mediaResponse);
                var mediaList = converter.Convert();
                mediaList.Pages++;
                var nextId = mediaResponse.NextMaxId;
                while (moreAvailable && mediaList.Pages < maxPages)
                {
                    instaUri = UriCreator.GetMediaListWithMaxIdUri(user.Value.Pk, nextId);
                    var nextMedia = await GetUserMediaListWithMaxIdAsync(instaUri);
                    mediaList.Pages++;
                    if (!nextMedia.Succeeded)
                        Result.Success($"Not all pages were downloaded: {nextMedia.Info.Message}", mediaList);
                    nextId = nextMedia.Value.NextMaxId;
                    moreAvailable = nextMedia.Value.MoreAvailable;
                    converter = ConvertersFabric.GetMediaListConverter(nextMedia.Value);
                    mediaList.AddRange(converter.Convert());
                }
                return Result.Success(mediaList);
            }
            return Result.UnExpectedResponse<InstaMediaList>(response, json);
        }

        public async Task<IResult<InstaMedia>> GetMediaByIdAsync(string mediaId)
        {
            ValidateUser();
            var mediaUri = UriCreator.GetMediaUri(mediaId);
            var request = HttpHelper.GetDefaultRequest(HttpMethod.Get, mediaUri, _deviceInfo);
            var response = await _httpRequestProcessor.SendAsync(request);
            var json = await response.Content.ReadAsStringAsync();
            if (response.StatusCode == HttpStatusCode.OK)
            {
                var mediaResponse = JsonConvert.DeserializeObject<InstaMediaListResponse>(json,
                    new InstaMediaListDataConverter());
                if (mediaResponse.Medias?.Count != 1)
                {
                    var errorMessage = $"Got wrong media count for request with media id={mediaId}";
                    _logger?.LogInfo(errorMessage);
                    return Result.Fail<InstaMedia>(errorMessage);
                }
                var converter = ConvertersFabric.GetSingleMediaConverter(mediaResponse.Medias.FirstOrDefault());
                return Result.Success(converter.Convert());
            }
            return Result.UnExpectedResponse<InstaMedia>(response, json);
        }

        public async Task<IResult<InstaUser>> GetUserAsync(string username)
        {
            ValidateUser();
            var userUri = UriCreator.GetUserUri(username);
            var request = HttpHelper.GetDefaultRequest(HttpMethod.Get, userUri, _deviceInfo);
            request.Properties.Add(new KeyValuePair<string, object>(InstaApiConstants.HEADER_TIMEZONE,
                InstaApiConstants.TIMEZONE_OFFSET.ToString()));
            request.Properties.Add(new KeyValuePair<string, object>(InstaApiConstants.HEADER_COUNT, "1"));
            request.Properties.Add(
                new KeyValuePair<string, object>(InstaApiConstants.HEADER_RANK_TOKEN, _user.RankToken));
            var response = await _httpRequestProcessor.SendAsync(request);
            var json = await response.Content.ReadAsStringAsync();
            if (response.StatusCode == HttpStatusCode.OK)
            {
                var userInfo = JsonConvert.DeserializeObject<InstaSearchUserResponse>(json);
                var user = userInfo.Users?.FirstOrDefault(u => u.UserName == username);
                if (user == null)
                {
                    var errorMessage = $"Can't find this user: {username}";
                    _logger?.LogInfo(errorMessage);
                    return Result.Fail<InstaUser>(errorMessage);
                }
                if (string.IsNullOrEmpty(user.Pk))
                    Result.Fail<InstaCurrentUser>("Pk is null");
                var converter = ConvertersFabric.GetUserConverter(user);
                return Result.Success(converter.Convert());
            }
            return Result.UnExpectedResponse<InstaUser>(response, json);
        }


        public async Task<IResult<InstaCurrentUser>> GetCurrentUserAsync()
        {
            ValidateUser();
            ValidateLoggedIn();
            var instaUri = UriCreator.GetCurrentUserUri();
            var fields = new Dictionary<string, string>
            {
                {"_uuid", _deviceInfo.DeviceGuid.ToString()},
                {"_uid", _user.LoggedInUder.Pk},
                {"_csrftoken", _user.CsrfToken}
            };
            var request = HttpHelper.GetDefaultRequest(HttpMethod.Post, instaUri, _deviceInfo);
            request.Content = new FormUrlEncodedContent(fields);
            var response = await _httpRequestProcessor.SendAsync(request);
            var json = await response.Content.ReadAsStringAsync();
            if (response.StatusCode == HttpStatusCode.OK)
            {
                var user = JsonConvert.DeserializeObject<InstaCurrentUserResponse>(json,
                    new InstaCurrentUserDataConverter());
                if (string.IsNullOrEmpty(user.Pk))
                    Result.Fail<InstaCurrentUser>("Pk is null");
                var converter = ConvertersFabric.GetCurrentUserConverter(user);
                var userConverted = converter.Convert();
                return Result.Success(userConverted);
            }
            return Result.UnExpectedResponse<InstaCurrentUser>(response, json);
        }

        public async Task<IResult<InstaTagFeed>> GetTagFeedAsync(string tag, int maxPages = 0)
        {
            ValidateUser();
            ValidateLoggedIn();
            if (maxPages == 0) maxPages = int.MaxValue;
            var userFeedUri = UriCreator.GetTagFeedUri(tag);
            var request = HttpHelper.GetDefaultRequest(HttpMethod.Get, userFeedUri, _deviceInfo);
            var response = await _httpRequestProcessor.SendAsync(request);
            var json = await response.Content.ReadAsStringAsync();
            if (response.StatusCode == HttpStatusCode.OK)
            {
                var feedResponse = JsonConvert.DeserializeObject<InstaTagFeedResponse>(json,
                    new InstaTagFeedDataConverter());
                var converter = ConvertersFabric.GetTagFeedConverter(feedResponse);
                var tagFeed = converter.Convert();
                tagFeed.Medias.Pages++;
                var nextId = feedResponse.NextMaxId;
                var moreAvailable = feedResponse.MoreAvailable;
                while (moreAvailable && tagFeed.Medias.Pages < maxPages)
                {
                    var nextMedia = await GetTagFeedWithMaxIdAsync(tag, nextId);
                    tagFeed.Medias.Pages++;
                    if (!nextMedia.Succeeded)
                        return Result.Success($"Not all pages was downloaded: {nextMedia.Info.Message}", tagFeed);
                    nextId = nextMedia.Value.NextMaxId;
                    moreAvailable = nextMedia.Value.MoreAvailable;
                    tagFeed.Medias.AddRange(ConvertersFabric.GetMediaListConverter(nextMedia.Value).Convert());
                }
                return Result.Success(tagFeed);
            }
            return Result.UnExpectedResponse<InstaTagFeed>(response, json);
        }

        public async Task<IResult<InstaUserShortList>> GetUserFollowersAsync(string username, int maxPages = 0)
        {
            ValidateUser();
            ValidateLoggedIn();
            try
            {
                if (maxPages == 0) maxPages = int.MaxValue;
                var user = await GetUserAsync(username);
                var userFollowersUri = UriCreator.GetUserFollowersUri(user.Value.Pk, _user.RankToken);
                var followers = new InstaUserShortList();
                var followersResponse = await GetUserListByURIAsync(userFollowersUri);
                if (!followersResponse.Succeeded)
                    Result.Fail(followersResponse.Info, (InstaUserList)null);
                followers.AddRange(
                    followersResponse.Value.Items.Select(ConvertersFabric.GetUserShortConverter)
                        .Select(converter => converter.Convert()));
                if (!followersResponse.Value.IsBigList) return Result.Success(followers);
                var pages = 1;
                while (!string.IsNullOrEmpty(followersResponse.Value.NextMaxId) && pages < maxPages)
                {
                    var nextFollowersUri =
                        UriCreator.GetUserFollowersUri(user.Value.Pk, _user.RankToken,
                            followersResponse.Value.NextMaxId);
                    followersResponse = await GetUserListByURIAsync(nextFollowersUri);
                    if (!followersResponse.Succeeded)
                        return Result.Success($"Not all pages was downloaded: {followersResponse.Info.Message}",
                            followers);
                    followers.AddRange(
                        followersResponse.Value.Items.Select(ConvertersFabric.GetUserShortConverter)
                            .Select(converter => converter.Convert()));
                    pages++;
                }
                return Result.Success(followers);
            }
            catch (Exception exception)
            {
                LogException(exception);
                return Result.Fail(exception.Message, (InstaUserShortList)null);
            }
        }

        public async Task<IResult<InstaUserShortList>> GetUserFollowingAsync(string username, int maxPages = 0)
        {
            ValidateUser();
            ValidateLoggedIn();
            try
            {
                if (maxPages == 0) maxPages = int.MaxValue;
                var user = await GetUserAsync(username);
                var userFeedUri = UriCreator.GetUserFollowingUri(user.Value.Pk, _user.RankToken);
                var following = new InstaUserShortList();
                var userListResponse = await GetUserListByURIAsync(userFeedUri);
                if (!userListResponse.Succeeded)
                    Result.Fail(userListResponse.Info, following);
                following.AddRange(
                    userListResponse.Value.Items.Select(ConvertersFabric.GetUserShortConverter)
                        .Select(converter => converter.Convert()));
                if (!userListResponse.Value.IsBigList) return Result.Success(following);
                var pages = 1;
                while (!string.IsNullOrEmpty(userListResponse.Value.NextMaxId) && pages < maxPages)
                {
                    var nextUri =
                        UriCreator.GetUserFollowingUri(user.Value.Pk, _user.RankToken,
                            userListResponse.Value.NextMaxId);
                    userListResponse = await GetUserListByURIAsync(nextUri);
                    if (!userListResponse.Succeeded)
                        return Result.Success($"Not all pages was downloaded: {userListResponse.Info.Message}",
                            following);
                    following.AddRange(
                        userListResponse.Value.Items.Select(ConvertersFabric.GetUserShortConverter)
                            .Select(converter => converter.Convert()));
                    pages++;
                }
                return Result.Success(following);
            }
            catch (Exception exception)
            {
                LogException(exception);
                return Result.Fail(exception.Message, (InstaUserShortList)null);
            }
        }


        public async Task<IResult<InstaUserShortList>> GetCurrentUserFollowersAsync(int maxPages = 0)
        {
            ValidateUser();
            return await GetUserFollowersAsync(_user.UserName, maxPages);
        }

        public async Task<IResult<InstaMediaList>> GetUserTagsAsync(string username, int maxPages = 0)
        {
            ValidateUser();
            ValidateLoggedIn();
            try
            {
                if (maxPages == 0) maxPages = int.MaxValue;
                var user = await GetUserAsync(username);
                if (!user.Succeeded || string.IsNullOrEmpty(user.Value.Pk))
                    return Result.Fail($"Unable to get user {username}", (InstaMediaList)null);
                var uri = UriCreator.GetUserTagsUri(user.Value?.Pk, _user.RankToken);
                var request = HttpHelper.GetDefaultRequest(HttpMethod.Get, uri, _deviceInfo);
                var response = await _httpRequestProcessor.SendAsync(request);
                var json = await response.Content.ReadAsStringAsync();
                var userTags = new InstaMediaList();
                if (response.StatusCode != HttpStatusCode.OK) return Result.Fail("", (InstaMediaList)null);
                var mediaResponse = JsonConvert.DeserializeObject<InstaMediaListResponse>(json,
                    new InstaMediaListDataConverter());
                var nextId = mediaResponse.NextMaxId;
                userTags.AddRange(
                    mediaResponse.Medias.Select(ConvertersFabric.GetSingleMediaConverter)
                        .Select(converter => converter.Convert()));
                var pages = 1;
                while (!string.IsNullOrEmpty(nextId) && pages < maxPages)
                {
                    uri = UriCreator.GetUserTagsUri(user.Value?.Pk, _user.RankToken, nextId);
                    var nextMedia = await GetUserMediaListWithMaxIdAsync(uri);
                    if (!nextMedia.Succeeded)
                        Result.Success($"Not all pages was downloaded: {nextMedia.Info.Message}", userTags);
                    nextId = nextMedia.Value.NextMaxId;
                    userTags.AddRange(
                        mediaResponse.Medias.Select(ConvertersFabric.GetSingleMediaConverter)
                            .Select(converter => converter.Convert()));
                    pages++;
                }
                return Result.Success(userTags);
            }
            catch (Exception exception)
            {
                LogException(exception);
                return Result.Fail(exception.Message, (InstaMediaList)null);
            }
        }


        public async Task<IResult<InstaDirectInboxContainer>> GetDirectInboxAsync()
        {
            ValidateUser();
            ValidateLoggedIn();
            try
            {
                var directInboxUri = UriCreator.GetDirectInboxUri();
                var request = HttpHelper.GetDefaultRequest(HttpMethod.Get, directInboxUri, _deviceInfo);
                var response = await _httpRequestProcessor.SendAsync(request);
                var json = await response.Content.ReadAsStringAsync();
                if (response.StatusCode != HttpStatusCode.OK) return Result.Fail("", (InstaDirectInboxContainer)null);
                var inboxResponse = JsonConvert.DeserializeObject<InstaDirectInboxContainerResponse>(json);
                var converter = ConvertersFabric.GetDirectInboxConverter(inboxResponse);
                return Result.Success(converter.Convert());
            }
            catch (Exception exception)
            {
                LogException(exception);
                return Result.Fail<InstaDirectInboxContainer>(exception);
            }
        }

        public async Task<IResult<InstaDirectInboxThread>> GetDirectInboxThreadAsync(string threadId)
        {
            ValidateUser();
            ValidateLoggedIn();
            try
            {
                var directInboxUri = UriCreator.GetDirectInboxThreadUri(threadId);
                var request = HttpHelper.GetDefaultRequest(HttpMethod.Get, directInboxUri, _deviceInfo);
                var response = await _httpRequestProcessor.SendAsync(request);
                var json = await response.Content.ReadAsStringAsync();
                if (response.StatusCode != HttpStatusCode.OK) return Result.Fail("", (InstaDirectInboxThread)null);
                var threadResponse = JsonConvert.DeserializeObject<InstaDirectInboxThreadResponse>(json,
                    new InstaThreadDataConverter());
                var converter = ConvertersFabric.GetDirectThreadConverter(threadResponse);
                return Result.Success(converter.Convert());
            }
            catch (Exception exception)
            {
                LogException(exception);
                return Result.Fail<InstaDirectInboxThread>(exception);
            }
        }

        public async Task<IResult<bool>> SendDirectMessage(string recipients, string threadIds, string text)
        {
            ValidateUser();
            ValidateLoggedIn();
            try
            {
                var directSendMessageUri = UriCreator.GetDirectSendMessageUri();

                var request = HttpHelper.GetDefaultRequest(HttpMethod.Post, directSendMessageUri, _deviceInfo);

                var fields = new Dictionary<string, string> { { "text", text } };

                if (!string.IsNullOrEmpty(recipients))
                    fields.Add("recipient_users", "[[" + recipients + "]]");
                else if (!string.IsNullOrEmpty(threadIds))
                    fields.Add("thread_ids", "[" + threadIds + "]");
                else
                    return Result.Fail<bool>("Please provide at least one recipient or thread.");

                request.Content = new FormUrlEncodedContent(fields);
                var response = await _httpRequestProcessor.SendAsync(request);
                var json = await response.Content.ReadAsStringAsync();
                if (response.StatusCode != HttpStatusCode.OK) return Result.UnExpectedResponse<bool>(response, json);
                var result = JsonConvert.DeserializeObject<InstaSendDirectMessageResponse>(json);
                return result.IsOk() ? Result.Success(true) : Result.Fail<bool>(result.Status);
            }
            catch (Exception exception)
            {
                LogException(exception);
                return Result.Fail<bool>(exception);
            }
        }

        public async Task<IResult<InstaRecipientThreads>> GetRecentRecipientsAsync()
        {
            ValidateUser();
            ValidateLoggedIn();
            var userUri = UriCreator.GetRecentRecipientsUri();
            var request = HttpHelper.GetDefaultRequest(HttpMethod.Get, userUri, _deviceInfo);
            var response = await _httpRequestProcessor.SendAsync(request);
            var json = await response.Content.ReadAsStringAsync();

            if (response.StatusCode != HttpStatusCode.OK)
                return Result.UnExpectedResponse<InstaRecipientThreads>(response, json);
            var responseRecipients = JsonConvert.DeserializeObject<InstaRecentRecipientsResponse>(json);
            var converter = ConvertersFabric.GetRecipientsConverter(responseRecipients);
            return Result.Success(converter.Convert());
        }

        public async Task<IResult<InstaRecipientThreads>> GetRankedRecipientsAsync()
        {
            ValidateUser();
            ValidateLoggedIn();
            var userUri = UriCreator.GetRankedRecipientsUri();
            var request = HttpHelper.GetDefaultRequest(HttpMethod.Get, userUri, _deviceInfo);
            var response = await _httpRequestProcessor.SendAsync(request);
            var json = await response.Content.ReadAsStringAsync();
            if (response.StatusCode != HttpStatusCode.OK)
                return Result.UnExpectedResponse<InstaRecipientThreads>(response, json);
            var responseRecipients = JsonConvert.DeserializeObject<InstaRankedRecipientsResponse>(json);
            var converter = ConvertersFabric.GetRecipientsConverter(responseRecipients);
            return Result.Success(converter.Convert());
        }

        public async Task<IResult<InstaActivityFeed>> GetRecentActivityAsync(int maxPages = 0)
        {
            var uri = UriCreator.GetRecentActivityUri();
            return await GetRecentActivityInternalAsync(uri, maxPages);
        }

        public async Task<IResult<InstaActivityFeed>> GetFollowingRecentActivityAsync(int maxPages = 0)
        {
            var uri = UriCreator.GetFollowingRecentActivityUri();
            return await GetRecentActivityInternalAsync(uri, maxPages);
        }


        public async Task<IResult<bool>> CheckpointAsync(string checkPointUrl)
        {
            if (string.IsNullOrEmpty(checkPointUrl)) return Result.Fail("Empty checkpoint URL", false);
            var instaUri = new Uri(checkPointUrl);
            var request = HttpHelper.GetDefaultRequest(HttpMethod.Get, instaUri, _deviceInfo);
            var response = await _httpRequestProcessor.SendAsync(request);
            var json = await response.Content.ReadAsStringAsync();
            if (response.StatusCode == HttpStatusCode.OK) return Result.Success(true);
            return Result.UnExpectedResponse<bool>(response, json);
        }


        public async Task<IResult<bool>> LikeMediaAsync(string mediaId)
        {
            return await LikeUnlikeMediaInternal(mediaId, UriCreator.GetLikeMediaUri(mediaId));
        }

        public async Task<IResult<bool>> UnLikeMediaAsync(string mediaId)
        {
            return await LikeUnlikeMediaInternal(mediaId, UriCreator.GetUnLikeMediaUri(mediaId));
        }


        public async Task<IResult<bool>> LikeUnlikeMediaInternal(string mediaId, Uri instaUri)
        {
            ValidateUser();
            ValidateLoggedIn();
            try
            {
                var fields = new Dictionary<string, string>
                {
                    {"_uuid", _deviceInfo.DeviceGuid.ToString()},
                    {"_uid", _user.LoggedInUder.Pk},
                    {"_csrftoken", _user.CsrfToken},
                    {"media_id", mediaId}
                };
                var request = HttpHelper.GetSignedRequest(HttpMethod.Post, instaUri, _deviceInfo, fields);
                var response = await _httpRequestProcessor.SendAsync(request);
                var json = await response.Content.ReadAsStringAsync();
                if (response.StatusCode == HttpStatusCode.OK)
                    return Result.Success(true);
                return Result.UnExpectedResponse<bool>(response, json);
            }
            catch (Exception exception)
            {
                LogException(exception);
                return Result.Fail(exception.Message, false);
            }
        }

        public async Task<IResult<InstaCommentList>> GetMediaCommentsAsync(string mediaId, int maxPages = 0)
        {
            ValidateUser();
            ValidateLoggedIn();
            try
            {
                if (maxPages == 0) maxPages = int.MaxValue;
                var commentsUri = UriCreator.GetMediaCommentsUri(mediaId);
                var request = HttpHelper.GetDefaultRequest(HttpMethod.Get, commentsUri, _deviceInfo);
                var response = await _httpRequestProcessor.SendAsync(request);
                var json = await response.Content.ReadAsStringAsync();
                if (response.StatusCode != HttpStatusCode.OK)
                    return Result.Fail($"Unexpected response status: {response.StatusCode}", (InstaCommentList)null);
                var commentListResponse = JsonConvert.DeserializeObject<InstaCommentListResponse>(json);
                var converter = ConvertersFabric.GetCommentListConverter(commentListResponse);
                var instaComments = converter.Convert();
                instaComments.Pages++;
                var nextId = commentListResponse.NextMaxId;
                var moreAvailable = commentListResponse.MoreComentsAvailable;
                while (moreAvailable && instaComments.Pages < maxPages)
                {
                    if (string.IsNullOrEmpty(nextId)) break;
                    var nextComments = await GetCommentListWithMaxIdAsync(mediaId, nextId);
                    if (!nextComments.Succeeded)
                        Result.Success($"Not all pages was downloaded: {nextComments.Info.Message}", instaComments);
                    nextId = nextComments.Value.NextMaxId;
                    moreAvailable = nextComments.Value.MoreComentsAvailable;
                    converter = ConvertersFabric.GetCommentListConverter(nextComments.Value);
                    instaComments.Comments.AddRange(converter.Convert().Comments);
                    instaComments.Pages++;
                }
                return Result.Success(instaComments);
            }
            catch (Exception exception)
            {
                LogException(exception);
                return Result.Fail<InstaCommentList>(exception);
            }
        }

        public async Task<IResult<InstaLikersList>> GetMediaLikersAsync(string mediaId)
        {
            ValidateUser();
            ValidateLoggedIn();
            try
            {
                var likersUri = UriCreator.GetMediaLikersUri(mediaId);
                var request = HttpHelper.GetDefaultRequest(HttpMethod.Get, likersUri, _deviceInfo);
                var response = await _httpRequestProcessor.SendAsync(request);
                var json = await response.Content.ReadAsStringAsync();
                if (response.StatusCode != HttpStatusCode.OK)
                    return Result.UnExpectedResponse<InstaLikersList>(response, json);
                var likers = new InstaLikersList();
                var mediaLikersResponse = JsonConvert.DeserializeObject<InstaMediaLikersResponse>(json);
                likers.UsersCount = mediaLikersResponse.UsersCount;
                if (mediaLikersResponse.UsersCount < 1) return Result.Success(likers);
                likers.AddRange(
                    mediaLikersResponse.Users.Select(ConvertersFabric.GetUserShortConverter)
                        .Select(converter => converter.Convert()));
                return Result.Success(likers);
            }
            catch (Exception exception)
            {
                LogException(exception);
                return Result.Fail<InstaLikersList>(exception);
            }
        }

        public async Task<IResult<InstaFriendshipStatus>> FollowUserAsync(long userId)
        {
            return await FollowUnfollowUserInternal(userId, UriCreator.GetFollowUserUri(userId));
        }

        public async Task<IResult<InstaFriendshipStatus>> UnFollowUserAsync(long userId)
        {
            return await FollowUnfollowUserInternal(userId, UriCreator.GetUnFollowUserUri(userId));
        }


        public async Task<IResult<InstaUserShort>> SetAccountPrivateAsync()
        {
            ValidateUser();
            ValidateLoggedIn();
            try
            {
                var instaUri = UriCreator.GetUriSetAccountPrivate();
                var fields = new Dictionary<string, string>
                {
                    {"_uuid", _deviceInfo.DeviceGuid.ToString()},
                    {"_uid", _user.LoggedInUder.Pk},
                    {"_csrftoken", _user.CsrfToken}
                };
                var hash = CryptoHelper.CalculateHash(InstaApiConstants.IG_SIGNATURE_KEY,
                    JsonConvert.SerializeObject(fields));
                var payload = JsonConvert.SerializeObject(fields);
                var signature = $"{hash}.{Uri.EscapeDataString(payload)}";
                var request = HttpHelper.GetDefaultRequest(HttpMethod.Post, instaUri, _deviceInfo);
                request.Content = new FormUrlEncodedContent(fields);
                request.Properties.Add(InstaApiConstants.HEADER_IG_SIGNATURE, signature);
                request.Properties.Add(InstaApiConstants.HEADER_IG_SIGNATURE_KEY_VERSION,
                    InstaApiConstants.IG_SIGNATURE_KEY_VERSION);
                var response = await _httpRequestProcessor.SendAsync(request);
                var json = await response.Content.ReadAsStringAsync();
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    var userInfoUpdated =
                        JsonConvert.DeserializeObject<InstaUserShortResponse>(json, new InstaUserShortDataConverter());
                    if (string.IsNullOrEmpty(userInfoUpdated.Pk))
                        return Result.Fail<InstaUserShort>("Pk is null or empty");
                    var converter = ConvertersFabric.GetUserShortConverter(userInfoUpdated);
                    return Result.Success(converter.Convert());
                }
                return Result.UnExpectedResponse<InstaUserShort>(response, json);
            }
            catch (Exception exception)
            {
                LogException(exception);
                return Result.Fail(exception.Message, (InstaUserShort)null);
            }
        }

        public async Task<IResult<InstaUserShort>> SetAccountPublicAsync()
        {
            ValidateUser();
            ValidateLoggedIn();
            try
            {
                var instaUri = UriCreator.GetUriSetAccountPublic();
                var fields = new Dictionary<string, string>
                {
                    {"_uuid", _deviceInfo.DeviceGuid.ToString()},
                    {"_uid", _user.LoggedInUder.Pk},
                    {"_csrftoken", _user.CsrfToken}
                };
                var hash = CryptoHelper.CalculateHash(InstaApiConstants.IG_SIGNATURE_KEY,
                    JsonConvert.SerializeObject(fields));
                var payload = JsonConvert.SerializeObject(fields);
                var signature = $"{hash}.{Uri.EscapeDataString(payload)}";
                var request = HttpHelper.GetDefaultRequest(HttpMethod.Post, instaUri, _deviceInfo);
                request.Content = new FormUrlEncodedContent(fields);
                request.Properties.Add(InstaApiConstants.HEADER_IG_SIGNATURE, signature);
                request.Properties.Add(InstaApiConstants.HEADER_IG_SIGNATURE_KEY_VERSION,
                    InstaApiConstants.IG_SIGNATURE_KEY_VERSION);
                var response = await _httpRequestProcessor.SendAsync(request);
                var json = await response.Content.ReadAsStringAsync();
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    var userInfoUpdated =
                        JsonConvert.DeserializeObject<InstaUserShortResponse>(json, new InstaUserShortDataConverter());
                    if (string.IsNullOrEmpty(userInfoUpdated.Pk))
                        return Result.Fail<InstaUserShort>("Pk is null or empty");
                    var converter = ConvertersFabric.GetUserShortConverter(userInfoUpdated);
                    return Result.Success(converter.Convert());
                }
                return Result.UnExpectedResponse<InstaUserShort>(response, json);
            }
            catch (Exception exception)
            {
                LogException(exception);
                return Result.Fail(exception.Message, (InstaUserShort)null);
            }
        }


        public async Task<IResult<InstaComment>> CommentMediaAsync(string mediaId, string text)
        {
            ValidateUser();
            ValidateLoggedIn();
            try
            {
                var instaUri = UriCreator.GetPostCommetUri(mediaId);
                var breadcrumb = CryptoHelper.GetCommentBreadCrumbEncoded(text);
                var fields = new Dictionary<string, string>
                {
                    {"user_breadcrumb", breadcrumb},
                    {"idempotence_token", Guid.NewGuid().ToString()},
                    {"_uuid", _deviceInfo.DeviceGuid.ToString()},
                    {"_uid", _user.LoggedInUder.Pk},
                    {"_csrftoken", _user.CsrfToken},
                    {"comment_text", text},
                    {"containermodule", "comments_feed_timeline"},
                    {"radio_type", "wifi-none"}
                };
                var request = HttpHelper.GetSignedRequest(HttpMethod.Post, instaUri, _deviceInfo, fields);
                var response = await _httpRequestProcessor.SendAsync(request);
                var json = await response.Content.ReadAsStringAsync();
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    var commentResponse = JsonConvert.DeserializeObject<InstaCommentResponse>(json,
                        new InstaCommentDataConverter());
                    var converter = ConvertersFabric.GetCommentConverter(commentResponse);
                    return Result.Success(converter.Convert());
                }
                return Result.UnExpectedResponse<InstaComment>(response, json);
            }
            catch (Exception exception)
            {
                return Result.Fail(exception.Message, (InstaComment)null);
            }
        }

        public async Task<IResult<bool>> DeleteCommentAsync(string mediaId, string commentId)
        {
            ValidateUser();
            ValidateLoggedIn();
            try
            {
                var instaUri = UriCreator.GetDeleteCommetUri(mediaId, commentId);
                var fields = new Dictionary<string, string>
                {
                    {"_uuid", _deviceInfo.DeviceGuid.ToString()},
                    {"_uid", _user.LoggedInUder.Pk},
                    {"_csrftoken", _user.CsrfToken}
                };
                var request = HttpHelper.GetSignedRequest(HttpMethod.Post, instaUri, _deviceInfo, fields);
                var response = await _httpRequestProcessor.SendAsync(request);
                var json = await response.Content.ReadAsStringAsync();
                if (response.StatusCode == HttpStatusCode.OK)
                    return Result.Success(true);
                return Result.UnExpectedResponse<bool>(response, json);
            }
            catch (Exception exception)
            {
                LogException(exception);
                return Result.Fail(exception.Message, false);
            }
        }

        public async Task<IResult<InstaMedia>> UploadPhotoAsync(InstaImage image, string caption)
        {
            ValidateUser();
            ValidateLoggedIn();
            try
            {
                var instaUri = UriCreator.GetUploadPhotoUri();
                var uploadId = ApiRequestMessage.GenerateUploadId();
                var requestContent = new MultipartFormDataContent(uploadId)
                {
                    {new StringContent(uploadId), "\"upload_id\""},
                    {new StringContent(_deviceInfo.DeviceGuid.ToString()), "\"_uuid\""},
                    {new StringContent(_user.CsrfToken), "\"_csrftoken\""},
                    {
                        new StringContent("{\"lib_name\":\"jt\",\"lib_version\":\"1.3.0\",\"quality\":\"87\"}"),
                        "\"image_compression\""
                    }
                };
                var imageContent = new ByteArrayContent(File.ReadAllBytes(image.URI));
                imageContent.Headers.Add("Content-Transfer-Encoding", "binary");
                imageContent.Headers.Add("Content-Type", "application/octet-stream");
                requestContent.Add(imageContent, "photo", $"pending_media_{ApiRequestMessage.GenerateUploadId()}.jpg");
                var request = HttpHelper.GetDefaultRequest(HttpMethod.Post, instaUri, _deviceInfo);
                request.Content = requestContent;
                var response = await _httpRequestProcessor.SendAsync(request);
                var json = await response.Content.ReadAsStringAsync();
                if (response.IsSuccessStatusCode)
                    return await ConfigurePhotoAsync(image, uploadId, caption);
                return Result.UnExpectedResponse<InstaMedia>(response, json);
            }
            catch (Exception exception)
            {
                return Result.Fail(exception.Message, (InstaMedia)null);
            }
        }

        public async Task<IResult<InstaMedia>> ConfigurePhotoAsync(InstaImage image, string uploadId, string caption)
        {
            ValidateUser();
            ValidateLoggedIn();
            try
            {
                var instaUri = UriCreator.GetMediaConfigureUri();
                var androidVersion =
                    AndroidVersion.FromString(_deviceInfo.FirmwareFingerprint.Split('/')[2].Split(':')[1]);
                if (androidVersion == null)
                    return Result.Fail("Unsupported android version", (InstaMedia)null);
                var data = new JObject
                {
                    {"_uuid", _deviceInfo.DeviceGuid.ToString()},
                    {"_uid", _user.LoggedInUder.Pk},
                    {"_csrftoken", _user.CsrfToken},
                    {"media_folder", "Camera"},
                    {"source_type", "4"},
                    {"caption", caption},
                    {"upload_id", uploadId},
                    {
                        "device", new JObject
                        {
                            {"manufacturer", _deviceInfo.HardwareManufacturer},
                            {"model", _deviceInfo.HardwareModel},
                            {"android_version", androidVersion.VersionNumber},
                            {"android_release", androidVersion.APILevel}
                        }
                    },
                    {
                        "edits", new JObject
                        {
                            {"crop_original_size", new JArray {image.Width, image.Height}},
                            {"crop_center", new JArray {0.0, -0.0}},
                            {"crop_zoom", 1}
                        }
                    },
                    {
                        "extra", new JObject
                        {
                            {"source_width", image.Width},
                            {"source_height", image.Height}
                        }
                    }
                };
                var request = HttpHelper.GetSignedRequest(HttpMethod.Post, instaUri, _deviceInfo, data);
                var response = await _httpRequestProcessor.SendAsync(request);
                var json = await response.Content.ReadAsStringAsync();
                if (response.IsSuccessStatusCode)
                {
                    var mediaResponse = JsonConvert.DeserializeObject<InstaMediaItemResponse>(json);
                    var converter = ConvertersFabric.GetSingleMediaConverter(mediaResponse);
                    return Result.Success(converter.Convert());
                }
                return Result.UnExpectedResponse<InstaMedia>(response, json);
            }
            catch (Exception exception)
            {
                LogException(exception);
                return Result.Fail(exception.Message, (InstaMedia)null);
            }
        }


        public async Task<IResult<InstaStoryFeed>> GetStoryFeedAsync()
        {
            ValidateUser();
            ValidateLoggedIn();

            try
            {
                var storyFeedUri = UriCreator.GetStoryFeedUri();
                var request = HttpHelper.GetDefaultRequest(HttpMethod.Get, storyFeedUri, _deviceInfo);
                var response = await _httpRequestProcessor.SendAsync(request);
                var json = await response.Content.ReadAsStringAsync();
                if (response.StatusCode != HttpStatusCode.OK) return Result.Fail("", (InstaStoryFeed)null);
                var storyFeedResponse = JsonConvert.DeserializeObject<InstaStoryFeedResponse>(json);
                var instaStoryFeed = ConvertersFabric.GetStoryFeedConverter(storyFeedResponse).Convert();
                return Result.Success(instaStoryFeed);
            }
            catch (Exception exception)
            {
                LogException(exception);
                return Result.Fail(exception.Message, (InstaStoryFeed)null);
            }
        }

        public async Task<IResult<InstaStory>> GetUserStoryAsync(long userId)
        {
            ValidateUser();
            ValidateLoggedIn();

            try
            {
                var userStoryUri = UriCreator.GetUserStoryUri(userId);
                var request = HttpHelper.GetDefaultRequest(HttpMethod.Get, userStoryUri, _deviceInfo);
                var response = await _httpRequestProcessor.SendAsync(request);
                var json = await response.Content.ReadAsStringAsync();

                if (response.StatusCode != HttpStatusCode.OK) return Result.Fail("", (InstaStory)null);
                var userStory = new InstaStory();
                var userStoryResponse = JsonConvert.DeserializeObject<InstaStoryResponse>(json);

                userStory = ConvertersFabric.GetStoryConverter(userStoryResponse).Convert();

                return Result.Success(userStory);
            }
            catch (Exception exception)
            {
                LogException(exception);
                return Result.Fail(exception.Message, (InstaStory)null);
            }
        }

        public async Task<IResult<InstaStoryMedia>> UploadStoryPhotoAsync(InstaImage image, string caption)
        {
            ValidateUser();
            ValidateLoggedIn();
            try
            {
                var instaUri = UriCreator.GetUploadPhotoUri();
                var uploadId = ApiRequestMessage.GenerateUploadId();
                var requestContent = new MultipartFormDataContent(uploadId)
                {
                    {new StringContent(uploadId), "\"upload_id\""},
                    {new StringContent(_deviceInfo.DeviceGuid.ToString()), "\"_uuid\""},
                    {new StringContent(_user.CsrfToken), "\"_csrftoken\""},
                    {
                        new StringContent("{\"lib_name\":\"jt\",\"lib_version\":\"1.3.0\",\"quality\":\"87\"}"),
                        "\"image_compression\""
                    }
                };
                var imageContent = new ByteArrayContent(File.ReadAllBytes(image.URI));
                imageContent.Headers.Add("Content-Transfer-Encoding", "binary");
                imageContent.Headers.Add("Content-Type", "application/octet-stream");
                requestContent.Add(imageContent, "photo", $"pending_media_{ApiRequestMessage.GenerateUploadId()}.jpg");
                var request = HttpHelper.GetDefaultRequest(HttpMethod.Post, instaUri, _deviceInfo);
                request.Content = requestContent;
                var response = await _httpRequestProcessor.SendAsync(request);
                var json = await response.Content.ReadAsStringAsync();
                if (response.IsSuccessStatusCode)
                    return await ConfigureStoryPhotoAsync(image, uploadId, caption);
                return Result.UnExpectedResponse<InstaStoryMedia>(response, json);
            }
            catch (Exception exception)
            {
                return Result.Fail(exception.Message, (InstaStoryMedia)null);
            }
        }

        public async Task<IResult<InstaStoryMedia>> ConfigureStoryPhotoAsync(InstaImage image, string uploadId,
            string caption)
        {
            ValidateUser();
            ValidateLoggedIn();
            try
            {
                var instaUri = UriCreator.GetStoryConfigureUri();
                var data = new JObject
                {
                    {"_uuid", _deviceInfo.DeviceGuid.ToString()},
                    {"_uid", _user.LoggedInUder.Pk},
                    {"_csrftoken", _user.CsrfToken},
                    {"source_type", "1"},
                    {"caption", caption},
                    {"upload_id", uploadId},
                    {"edits", new JObject()},
                    {"disable_comments", false},
                    {"configure_mode", 1},
                    {"camera_position", "unknown"}
                };
                var request = HttpHelper.GetSignedRequest(HttpMethod.Post, instaUri, _deviceInfo, data);
                var response = await _httpRequestProcessor.SendAsync(request);
                var json = await response.Content.ReadAsStringAsync();
                if (response.IsSuccessStatusCode)
                {
                    var mediaResponse = JsonConvert.DeserializeObject<InstaStoryMediaResponse>(json);
                    var converter = ConvertersFabric.GetStoryMediaConverter(mediaResponse);
                    return Result.Success(converter.Convert());
                }
                return Result.UnExpectedResponse<InstaStoryMedia>(response, json);
            }
            catch (Exception exception)
            {
                LogException(exception);
                return Result.Fail(exception.Message, (InstaStoryMedia)null);
            }
        }

        //public int _getVideoDurationMs()
        //{
        //    Buffer buffer;
        //    var start = buffer.indexOf(new Buffer('mvhd')) + 17;
        //    var timeScale = buffer.readUInt32BE(start, 4);
        //    var duration = buffer.readUInt32BE(start + 4, 4);
        //    var movieLength = duration / timeScale;

        //    return movieLength * 1000;
        //}
        public int getVideoDuration(byte[] buffer)
        {
            //  var buffer = new byte[] { 33, 49, 0, 32, 0, 0, 0, 0, 2, 230, 69, 56, 0, 1, 125, 181, 99, 99, 136, 122, 92, 1, 99, 196, 231, 90, 205, 20, 75, 233, 5, 103 };
            var start = Array.IndexOf(buffer, "mvhd") + 17;
            string result = System.Text.Encoding.UTF8.GetString(buffer);
            var timeScale = Array.IndexOf(buffer, "mvhd") + 17;
            var value = BitConverter.ToUInt32(buffer, start);
            var rs = BitConverter.ToUInt32(BitConverter.GetBytes(value).Reverse().ToArray(), 0);
            return 63000;
        }

        //        private void _sendChunkedRequest(session, url, job, sessionId, buffer, range, isSidecar)
        //        {
        //            var headers = {
        //        'job': job,
        //        'Host': 'upload.instagram.com',
        //        'Session-ID': sessionId,
        //        'Content-Type': 'application/octet-stream',
        //        'Content-Disposition': 'attachment; filename=\\\"video.mov\\\"',
        //        'Content-Length': buffer.length,
        //        'Content-Range': range
        //    };
        //    if(isSidecar) {
        //        headers['Cookie'] = 'sessionid=' + sessionId;
        //    }
        //    return new Request(session)
        //        .setMethod('POST')
        //        .setBodyType('body')
        //        .setUrl(url)
        //        .generateUUID()
        //        .setHeaders(headers)
        //        .transform(function(opts){
        //        opts.body = buffer;
        //        return opts;
        //    })
        //        .send()
        //}

        private string _generateSessionId(string uploadId)
        {
            var text = (uploadId ?? "") + '-';
            var possible = "0123456789";

            var random = new Random();
            for (var i = 0; i < 9; i++)
                text += possible[(random.Next(0, 9))];

            return text;
        }

        public async Task<IResult<InstaMedia>> UploadVideoAsync(InstaVideo video, string caption)
        {
            ValidateUser();
            ValidateLoggedIn();

            string uploadId = ApiRequestMessage.GenerateUploadId();
            string uuId = _deviceInfo.DeviceGuid.ToString();
            var webClient = new WebClient();
            byte[] videoData = webClient.DownloadData(video.Url);
            var videoDuration = getVideoDuration(videoData);
            Uri uploadVideoUri = UriCreator.GetUploadVideoUri();


            var requestContent = new MultipartFormDataContent(uploadId)
            {
                {new StringContent(uploadId), "\"upload_id\""},
                {new StringContent(_deviceInfo.DeviceGuid.ToString()), "\"_uuid\""},
                {new StringContent(_user.CsrfToken), "\"_csrftoken\""},
                {new StringContent(video.Type.ToString()),"\"media_type\""},
                {new StringContent("22400"),"\"upload_media_duration_ms\""},
                {new StringContent(video.Height.ToString()),"\"upload_media_height\""},
                {new StringContent(video.Width.ToString()),"\"upload_media_width\""},
            };

            var request = HttpHelper.GetDefaultRequest(HttpMethod.Post, uploadVideoUri, _deviceInfo);
            request.Content = requestContent;
            var response = await _httpRequestProcessor.SendAsync(request);

            try
            {
                string jsonContent = await response.Content.ReadAsStringAsync();
                JObject node = JObject.Parse(jsonContent);

                if (node == null)
                {
                    throw new ArgumentNullException();
                }

                if (node.TryGetValue("video_upload_urls", StringComparison.CurrentCulture, out JToken value))
                {
                    dynamic urls = value;
                    if (urls.Count <= 0)
                    {
                        throw new ArgumentNullException();
                    }

                    if (string.IsNullOrEmpty((string)urls[3].url) || string.IsNullOrEmpty((string)urls[3].job))
                    {
                        throw new ArgumentNullException();
                    }


                    //long chunk = Convert.ToInt64(Math.Floor(videoData.Length / 4.0));
                    int chunk = 204800;
                    long last = (videoData.Length - (chunk * 2));
                    List<byte> data = videoData.ToList();
                    var sessionId = _generateSessionId(uploadId);
                    
                    //var chunks = [];
                    //chunks.push({
                    //    data: buffer.slice(0, chunkLength),
                    //range: 'bytes ' + 0 + '-' + (chunkLength - 1) + '/' + buffer.length
                    //});
                    //chunks.push({
                    //    data: buffer.slice(chunkLength, buffer.length),
                    //range: 'bytes ' + chunkLength + '-' + (buffer.length - 1) + '/' + buffer.length
                    //});

                    for (int i = 0; i < 2; i++)
                    {
                        long start = (i * chunk);
                        long end = ((i + 1) * chunk) + ((i == 1) ? last : 0);

                        #region Upload chunk

                        Uri chunkUri = new Uri(url);

                        var uploadChunkRequestContent = new MultipartFormDataContent(sessionId)
                        {
                            {new StringContent("$Version=1"), "\"Cookie2\""},
                            {new StringContent(sessionId), "\"Session-ID\""},
                            {new StringContent((string)urls[i].job),"\"job\""}
                        };
//var headers = new[] {
//        'job': job,
//        'Host': 'upload.instagram.com',
//        'Session-ID': sessionId,
//        'Content-Type': 'application/octet-stream',
//        'Content-Disposition': 'attachment; filename=\\\"video.mov\\\"',
//        'Content-Length': buffer.length,
//        'Content-Range': range
//    };

                    byte[] chunkData = data.GetRange((int)start, (int)(end - start)).ToArray();
                        var chunkContent = new ByteArrayContent(chunkData);
                        chunkContent.Headers.Add("Content-Transfer-Encoding", "binary");
                        chunkContent.Headers.Add("Content-Type", "application/octet-stream");
                        chunkContent.Headers.Add("Content-Length", (end - start).ToString());
                        chunkContent.Headers.Add("Content-Range", $"bytes {start}-{end - 1}/{videoData.Length}");
                        uploadChunkRequestContent.Add(chunkContent, "video", $"pending_media_{uploadId}.mp4");
                        var uploadChunkRequest = HttpHelper.GetDefaultRequest(HttpMethod.Post, chunkUri, _deviceInfo);
                        uploadChunkRequest.Content = uploadChunkRequestContent;
                        var uploadChunkResponse = await _httpRequestProcessor.SendAsync(uploadChunkRequest);
                        var result = await uploadChunkResponse.Content.ReadAsStringAsync();
                        //{ delay: '6500', durationms: 15079, uploadId: '1513960887281' }
                        if (uploadChunkResponse.IsSuccessStatusCode)
                        {
                            continue;
                        }

                        return Result.UnExpectedResponse<InstaMedia>(uploadChunkResponse, result);
                        #endregion
                    }

                    var uploadThumbnailResult = await UploadVideoThumbnail(thumbnail, uploadId);
                }
            }
            catch (Exception exception)
            {
                return Result.Fail(exception.Message, (InstaMedia)null);
            }

            return await ConfigureTimelineVideoAsync(video, uploadId, caption);

            // uplao9d preview

            //Uri uri = new Uri(HttpUrl + "upload/photo/");
            //HttpWebRequest request = (HttpWebRequest)WebRequest.Create(uri);
            //request.Method = "POST";
            //request.UserAgent = UserAgent;
            //request.Host = "i.instagram.com";
            //request.Accept = "*/*";
            //request.ContentType = $"multipart/form-data; boundary={uuid}";
            //request.Headers.Add("Cookie2", "$Version=1");
            //request.UseDefaultCredentials = true;
            //request.AllowAutoRedirect = false;
            //request.CookieContainer = new CookieContainer();
            //request.CookieContainer.Add(uri, _cookies);
            //request.KeepAlive = true;

            //string data = string.Empty;

            //data += $"\r\n\r\n--{uuid}\r\n";
            //data += $"Content-Disposition: form-data; name=\"upload_id\"\r\n";
            //data += $"\r\n";
            //data += $"{upload_id}\r\n";
            //data += $"--{uuid}\r\n";
            //data += $"Content-Disposition: form-data; name=\"_uuid\"\r\n";
            //data += $"\r\n";
            //data += $"{uuid}\r\n";
            //data += $"--{uuid}\r\n";
            //data += $"Content-Disposition: form-data; name=\"_csrftoken\"\r\n";
            //data += $"\r\n";
            //data += $"{_csrftoken}\r\n";
            //data += $"--{uuid}\r\n";
            //data += $"Content-Disposition: form-data; name=\"image_compression\"\r\n";
            //data += $"\r\n";
            //data += "{\"lib_name\":\"jt\",\"lib_version\":\"1.3.0\",\"quality\":\"100\"}\r\n";
            //data += $"--{uuid}\r\n";
            //data += $"Content-Disposition: form-data; name=\"photo\"; filename=\"pending_media_{upload_id}.jpg\"\r\n";
            //data += $"Content-Type: application/octet-stream\r\n";
            //data += $"Content-Transfer-Encoding: binary\r\n\r\n";

        }

        public async Task<IResult<InstaMedia>> ConfigureTimelineVideoAsync(InstaVideo video, string uploadId, string caption)
        {
            ValidateUser();
            ValidateLoggedIn();
            try
            {
                var instaUri = UriCreator.GetTimelineVideoConfigureUri();
                int currentTime = (int)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;
                var androidVersion = AndroidVersion.FromString(_deviceInfo.FirmwareFingerprint.Split('/')[2].Split(':')[1]);
                if (androidVersion == null)
                {
                    return Result.Fail("Unsupported android version", (InstaMedia)null);
                }

                var data = new JObject
                {
                    {"_uuid", _deviceInfo.DeviceGuid.ToString()},
                    {"_uid", _user.LoggedInUder.Pk},
                    {"_csrftoken", _user.CsrfToken},
                    { "video_result", "deprecated" },
                    {"audio_muted", false },
                    {"trim_type", 0},
                    {"duration", 22.4},
                    {"client_timestamp", currentTime.ToString().Substring(0,10)},
                    {"caption", caption },
                    {"source_type", "camera"},
                    {"mas_opt_in", "NOT_PROMPTED" },
                    {"length", 22.4},
                    {"disable_comments", false },
                    {"filter_type", 0},
                    {"poster_frame_index", 0 },
                    {"geotag_enabled", false },
                    {"camera_position", "unknown"},
                    {"upload_id", uploadId},
                    {
                        "device", new JObject
                        {
                            {"manufacturer", _deviceInfo.HardwareManufacturer},
                            {"model", _deviceInfo.HardwareModel},
                            {"android_version", androidVersion.VersionNumber},
                            {"android_release", androidVersion.APILevel}
                        }
                    },
                    {
                        "edits", new JObject
                        {
                            {"filter_strength", 1}
                        }
                    },
                    {
                        "clips", new JArray{ new JObject
                            {
                                { "length", 22.4 },
                                { "cinema", "unsupported" },
                                { "original_length", 22.4 },
                                { "source_type", "camera" },
                                { "start_time", 0 },
                                { "trim_type", 0 },
                                { "camera_position", "back"}
                            }}
                    }
                };

                var request = HttpHelper.GetSignedRequest(HttpMethod.Post, instaUri, _deviceInfo, data);
                var response = await _httpRequestProcessor.SendAsync(request);
                var json = await response.Content.ReadAsStringAsync();
                if (response.IsSuccessStatusCode)
                {
                    var mediaResponse = JsonConvert.DeserializeObject<InstaMediaItemResponse>(json);
                    var converter = ConvertersFabric.GetSingleMediaConverter(mediaResponse);
                    return Result.Success(converter.Convert());
                }

                return Result.UnExpectedResponse<InstaMedia>(response, json);
            }
            catch (Exception exception)
            {
                LogException(exception);
                return Result.Fail(exception.Message, (InstaMedia)null);
            }

            // configure video

            //string configure = $"{{\"caption\":\"{caption}\",\"upload_id\":\"{uploadId}\",\"source_type\":\"3\", \"camera_position\":\"unknown\",\"extra\":{{\"source_width\":1280,\"source_height\":720}},\"clips\":[{{\"length\":10.0,\"creation_date\" :\"2016-04-09T19:03:32-0700\",\"source_type\":\"3\",\"camera_position\":\"back\"}}],\"poster_frame_index\":0,\"audio_muted\":false,\"filter_type\":\"0\",\"video_result\":\"deprecated\",\"_csrftoken\":\"{_user.CsrfToken}\",\"_uuid\":\"{uuId}\",\"_uid\":\"{_user.LoggedInUder.Pk}\"}}";
            //var signature = $"{_httpRequestProcessor.RequestMessage.GenerateSignature(configure)}.{_httpRequestProcessor.RequestMessage.GetMessageString()}";
            //byte[] signed = Encoding.UTF8.GetBytes("signed_body=" + Uri.EscapeDataString(signature + "." + configure) + "&ig_sig_key_version=4");

            //Uri uri = new Uri("https://media/configure/?video=1");

            //HttpWebRequest configureRequest = (HttpWebRequest)WebRequest.Create(uri);
            //configureRequest.Method = "POST";
            //configureRequest.UserAgent = InstaApiConstants.USER_AGENT;
            //configureRequest.Host = "i.instagram.com";
            //configureRequest.Accept = "*/*";
            //configureRequest.CookieContainer = new CookieContainer();
            //configureRequest.CookieContainer.Add(uri, _httpRequestProcessor.HttpHandler.CookieContainer.GetCookies(uri));
            //configureRequest.ContentLength = signed.Length;

            //try
            //{
            //    using (Stream stream = request.GetRequestStream())
            //    {
            //        stream.Write(signed, 0, signed.Length);
            //    }
            //}
        }

        public async Task<IResult<InstaStoryMedia>> UploadStoryVideoAsync(InstaVideo video, InstaImage thumbnail, string caption)
        {
            ValidateUser();
            ValidateLoggedIn();
            string uploadId = ApiRequestMessage.GenerateUploadId();
            string uuId = _deviceInfo.DeviceGuid.ToString();
            var webClient = new WebClient();
            byte[] videoData = webClient.DownloadData(video.Url);
            Uri uploadVideoUri = UriCreator.GetUploadVideoUri();

            var requestContent = new MultipartFormDataContent(uploadId)
            {
                {new StringContent(uploadId), "\"upload_id\""},
                {new StringContent(_deviceInfo.DeviceGuid.ToString()), "\"_uuid\""},
                {new StringContent(_user.CsrfToken), "\"_csrftoken\""},
                {new StringContent(video.Type.ToString()),"\"media_type\""}
            };

            var request = HttpHelper.GetDefaultRequest(HttpMethod.Post, uploadVideoUri, _deviceInfo);
            request.Content = requestContent;
            var response = await _httpRequestProcessor.SendAsync(request);

            try
            {
                string jsonContent = await response.Content.ReadAsStringAsync();
                JObject node = JObject.Parse(jsonContent);

                if (node == null)
                {
                    throw new ArgumentNullException();
                }

                if (node.TryGetValue("video_upload_urls", StringComparison.CurrentCulture, out JToken value))
                {
                    dynamic urls = value;
                    if (urls.Count <= 0)
                    {
                        throw new ArgumentNullException();
                    }

                    if (string.IsNullOrEmpty((string)urls[3].url) || string.IsNullOrEmpty((string)urls[3].job))
                    {
                        throw new ArgumentNullException();
                    }


                    //long chunk = Convert.ToInt64(Math.Floor(videoData.Length / 4.0));
                    int chunk = 204800;
                    long last = (videoData.Length - (chunk * 2));
                    List<byte> data = videoData.ToList();
                    var sessionId = _generateSessionId(uploadId);

                    //var chunks = [];
                    //chunks.push({
                    //    data: buffer.slice(0, chunkLength),
                    //range: 'bytes ' + 0 + '-' + (chunkLength - 1) + '/' + buffer.length
                    //});
                    //chunks.push({
                    //    data: buffer.slice(chunkLength, buffer.length),
                    //range: 'bytes ' + chunkLength + '-' + (buffer.length - 1) + '/' + buffer.length
                    //});

                    for (int i = 0; i < 2; i++)
                    {
                        long start = (i * chunk);

                        long end = ((i + 1) * chunk) + ((i == 1) ? last : 0);

                        #region Upload chunk

                        Uri chunkUri = new Uri(url);

                        var uploadChunkRequestContent = new MultipartFormDataContent(uploadId)
                        {
                            {new StringContent("upload.instagram.com"), "\"Host\""},
                            {new StringContent(sessionId), "\"Session-ID\""},
                            {new StringContent(job),"\"job\""}
                        };
                        //var headers = new[] {
                        //        'job': job,
                        //        'c': 'upload.instagram.com',
                        //        'Session-ID': sessionId,
                        //        'Content-Type': 'application/octet-stream',
                        //        'Content-Disposition': 'attachment; filename=\\\"video.mov\\\"',
                        //        'Content-Length': buffer.length,
                        //        'Content-Range': range
                        //    };

                        byte[] chunkData = data.GetRange((int)start, (int)(end - start)).ToArray();
                        var chunkContent = new ByteArrayContent(chunkData);
                        chunkContent.Headers.Add("Content-Transfer-Encoding", "binary");
                        chunkContent.Headers.Add("Content-Type", "application/octet-stream");
//                        chunkContent.Headers.Add("Host", "upload.instagram.com");
                        chunkContent.Headers.Add("Content-Length", (end - start).ToString());
                        chunkContent.Headers.Add("Content-Disposition", "attachment; filename=\"video.MOV\"");
                        chunkContent.Headers.Add("Content-Range", $"bytes {start}-{end - 1}/{videoData.Length}");

                        uploadChunkRequestContent.Add(chunkContent, "testvideo2", $"pending_media_{ApiRequestMessage.GenerateUploadId()}.mov");
                        var uploadChunkRequest = HttpHelper.GetDefaultRequest(HttpMethod.Post, chunkUri, _deviceInfo);
                        uploadChunkRequest.Content = uploadChunkRequestContent;
                        var uploadChunkResponse = await _httpRequestProcessor.SendAsync(uploadChunkRequest);
                        var result = await uploadChunkResponse.Content.ReadAsStringAsync();
                        //{ delay: '6500', durationms: 15079, uploadId: '1513960887281' }
                        if (uploadChunkResponse.IsSuccessStatusCode)
                        {
                            continue;
                        }
                        return Result.UnExpectedResponse<InstaStoryMedia>(uploadChunkResponse, result);
                        #endregion
                    }

                    var uploadThumbnailResult = await UploadVideoThumbnail(thumbnail, uploadId);
                }
            }
            catch (Exception exception)
            {
                return Result.Fail(exception.Message, (InstaStoryMedia)null);
            }

            return await ConfigureStoryVideoAsync(video, uploadId, caption);
        }

        private string GenerateSessionId(string uploadId)
        {
            var text = (uploadId ?? "") + '-';
            var possible = "0123456789";

            var random = new Random();
            for (var i = 0; i < 9; i++)
                text += possible[(random.Next(0, 9))];

            return text;
        }


        private async Task<IResult<InstaMedia>> UploadVideoThumbnail(InstaImage thumbnail, string uploadId)
        {
            try
            {
                var instaUri = UriCreator.GetUploadPhotoUri();
                var requestContent = new MultipartFormDataContent(uploadId)
                {
                    {new StringContent(uploadId), "\"upload_id\""},
                    {new StringContent(_deviceInfo.DeviceGuid.ToString()), "\"_uuid\""},
                    {new StringContent(_user.CsrfToken), "\"_csrftoken\""},
                    {
                        new StringContent("{\"lib_name\":\"jt\",\"lib_version\":\"1.3.0\",\"quality\":\"87\"}"),
                        "\"image_compression\""
                    }
                };
                var imageContent = new ByteArrayContent(File.ReadAllBytes(thumbnail.URI));
                imageContent.Headers.Add("Content-Transfer-Encoding", "binary");
                imageContent.Headers.Add("Content-Type", "application/octet-stream");
                requestContent.Add(imageContent, "photo", $"pending_media_{uploadId}.jpg");
                var request = HttpHelper.GetDefaultRequest(HttpMethod.Post, instaUri, _deviceInfo);
                request.Content = requestContent;
                var response = await _httpRequestProcessor.SendAsync(request);
                var json = await response.Content.ReadAsStringAsync();
                if (response.IsSuccessStatusCode)
                {
                    var mediaResponse = JsonConvert.DeserializeObject<InstaMediaItemResponse>(json);
                    var converter = ConvertersFabric.GetSingleMediaConverter(mediaResponse);
                    return Result.Success(converter.Convert());
                }

                return Result.UnExpectedResponse<InstaMedia>(response, json);
            }
            catch (Exception exception)
            {
                return Result.Fail(exception.Message, (InstaMedia)null);
            }
        }

        public async Task<IResult<InstaStoryMedia>> ConfigureStoryVideoAsync(InstaVideo video, string uploadId, string caption)
        {
            ValidateUser();
            ValidateLoggedIn();
            try
            {
                var instaUri = UriCreator.GetStoryVideoConfigureUri();
                var androidVersion = AndroidVersion.FromString(_deviceInfo.FirmwareFingerprint.Split('/')[2].Split(':')[1]);
                if (androidVersion == null)
                {
                    return Result.Fail("Unsupported android version", (InstaStoryMedia)null);
                }

                int currentTime = (int) (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;
                Random numberGenerator = new Random();

                var data = new JObject
                {
                    { "video_result", "deprecated"},
                    { "upload_id", uploadId},
                    { "poster_frame_index", 0},
                    { "length", 22 },
                    { "audio_muted", false },
                    { "filter_type", 0},
                    { "source_type", "4"},
                    //{"edits", new JObject()},
                    //{"disable_comments", false},
                    //{"camera_position", "unknown"},
                    {
                        "device", new JObject
                        {
                            {"manufacturer", _deviceInfo.HardwareManufacturer},
                            {"model", _deviceInfo.HardwareModel},
                            {"android_version", androidVersion.VersionNumber},
                            {"android_release", androidVersion.APILevel}
                        }
                    },
                    {
                        "extra", new JObject
                        {
                            {"source_width", video.Width},
                            {"source_height", video.Height}
                        }
                    },
                    {"_csrftoken", _user.CsrfToken},
                    { "_uuid", _deviceInfo.DeviceGuid.ToString()},
                    {"_uid", _user.LoggedInUder.Pk},
                    {"configure_mode", 1},
                    {"story_media_creation_date", currentTime - numberGenerator.Next(10, 20) },
                    {"client_shared_at", currentTime - numberGenerator.Next(3, 10) },
                    {"client_timestamp", currentTime },
                  //  {"caption", caption}
                };

                //string configure = $"{{\"caption\":\"{caption}\",\"upload_id\":\"{uploadId}\",\"source_type\":\"3\", \"camera_position\":\"unknown\",\"extra\":{{\"source_width\":1280,\"source_height\":720}},\"clips\":[{{\"length\":10.0,\"creation_date\" :\"2016-04-09T19:03:32-0700\",\"source_type\":\"3\",\"camera_position\":\"back\"}}],\"poster_frame_index\":0,\"audio_muted\":false,\"filter_type\":\"0\",\"video_result\":\"deprecated\",\"_csrftoken\":\"{_user.CsrfToken}\",\"_uuid\":\"{uuId}\",\"_uid\":\"{_user.LoggedInUder.Pk}\"}}";
                //var signature = $"{_httpRequestProcessor.RequestMessage.GenerateSignature(configure)}.{_httpRequestProcessor.RequestMessage.GetMessageString()}";
                //byte[] signed = Encoding.UTF8.GetBytes("signed_body=" + Uri.EscapeDataString(signature + "." + configure) + "&ig_sig_key_version=4");

                //Uri uri = new Uri("https://media/configure/?video=1");

                //HttpWebRequest configureRequest = (HttpWebRequest)WebRequest.Create(uri);
                //configureRequest.Method = "POST";
                //configureRequest.UserAgent = InstaApiConstants.USER_AGENT;
                //configureRequest.Host = "i.instagram.com";
                //configureRequest.Accept = "*/*";
                //configureRequest.CookieContainer = new CookieContainer();
                //configureRequest.CookieContainer.Add(uri, _httpRequestProcessor.HttpHandler.CookieContainer.GetCookies(uri));
                //configureRequest.ContentLength = signed.Length;

                var request = HttpHelper.GetSignedRequest(HttpMethod.Post, instaUri, _deviceInfo, data);
                var response = await _httpRequestProcessor.SendAsync(request);
                var json = await response.Content.ReadAsStringAsync();
                if (response.IsSuccessStatusCode)
                {
                    var mediaResponse = JsonConvert.DeserializeObject<InstaStoryMediaResponse>(json);
                    var converter = ConvertersFabric.GetStoryMediaConverter(mediaResponse);
                    return Result.Success(converter.Convert());
                }

                return Result.UnExpectedResponse<InstaStoryMedia>(response, json);
            }
            catch (Exception exception)
            {
                LogException(exception);
                return Result.Fail(exception.Message, (InstaStoryMedia)null);
            }
        }

        public async Task<IResult<bool>> ChangePasswordAsync(string oldPassword, string newPassword)
        {
            ValidateUser();
            ValidateLoggedIn();

            if (oldPassword == newPassword)
                return Result.Fail("The old password should not the same of the new password", false);

            try
            {
                var changePasswordUri = UriCreator.GetChangePasswordUri();

                var data = new JObject
                {
                    {"_uuid", _deviceInfo.DeviceGuid.ToString()},
                    {"_uid", _user.LoggedInUder.Pk},
                    {"_csrftoken", _user.CsrfToken},
                    {"old_password", oldPassword},
                    {"new_password1", newPassword},
                    {"new_password2", newPassword}
                };

                var request = HttpHelper.GetSignedRequest(HttpMethod.Get, changePasswordUri, _deviceInfo, data);
                var response = await _httpRequestProcessor.SendAsync(request);
                var json = await response.Content.ReadAsStringAsync();
                if (response.StatusCode == HttpStatusCode.OK)
                    return Result.Success(true); //If status code is OK, then the password is surely changed
                var error = JsonConvert.DeserializeObject<BadStatusErrorsResponse>(json);
                var errors = "";
                error.Message.Errors.ForEach(errorContent => errors += errorContent + "\n");
                return Result.Fail(errors, false);
            }
            catch (Exception exception)
            {
                return Result.Fail(exception.Message, false);
            }
        }

        public async Task<IResult<bool>> DeleteMediaAsync(string mediaId, InstaMediaType mediaType)
        {
            ValidateUser();
            ValidateLoggedIn();

            try
            {
                var deleteMediaUri = UriCreator.GetDeleteMediaUri(mediaId, mediaType);

                var data = new JObject
                {
                    {"_uuid", _deviceInfo.DeviceGuid.ToString()},
                    {"_uid", _user.LoggedInUder.Pk},
                    {"_csrftoken", _user.CsrfToken},
                    {"media_id", mediaId}
                };

                var request = HttpHelper.GetSignedRequest(HttpMethod.Get, deleteMediaUri, _deviceInfo, data);
                var response = await _httpRequestProcessor.SendAsync(request);
                var json = await response.Content.ReadAsStringAsync();
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    var deletedResponse = JsonConvert.DeserializeObject<DeleteResponse>(json);
                    return Result.Success(deletedResponse.IsDeleted);
                }
                var error = JsonConvert.DeserializeObject<BadStatusErrorsResponse>(json);
                var errors = "";
                error.Message.Errors.ForEach(errorContent => errors += errorContent + "\n");
                return Result.Fail(errors, false);
            }
            catch (Exception exception)
            {
                LogException(exception);
                return Result.Fail(exception.Message, false);
            }
        }

        public async Task<IResult<bool>> EditMediaAsync(string mediaId, string caption)
        {
            ValidateUser();
            ValidateLoggedIn();

            try
            {
                var editMediaUri = UriCreator.GetEditMediaUri(mediaId);

                var data = new JObject
                {
                    {"_uuid", _deviceInfo.DeviceGuid.ToString()},
                    {"_uid", _user.LoggedInUder.Pk},
                    {"_csrftoken", _user.CsrfToken},
                    {"caption_text", caption}
                };

                var request = HttpHelper.GetSignedRequest(HttpMethod.Get, editMediaUri, _deviceInfo, data);
                var response = await _httpRequestProcessor.SendAsync(request);
                var json = await response.Content.ReadAsStringAsync();
                if (response.StatusCode == HttpStatusCode.OK)
                    return
                        Result.Success(
                            true); //Technically Instagram returns the InstaMediaItem, but it is useless in our case, at this time.
                var error = JsonConvert.DeserializeObject<BadStatusResponse>(json);
                return Result.Fail(error.Message, false);
            }
            catch (Exception exception)
            {
                return Result.Fail(exception.Message, false);
            }
        }

        public async Task<IResult<InstaMediaList>> GetLikeFeedAsync(int maxPages = 0)
        {
            ValidateUser();
            if (maxPages == 0) maxPages = int.MaxValue;
            var instaUri = UriCreator.GetUserLikeFeedUri();
            var request = HttpHelper.GetDefaultRequest(HttpMethod.Get, instaUri, _deviceInfo);
            var response = await _httpRequestProcessor.SendAsync(request);
            var json = await response.Content.ReadAsStringAsync();
            if (response.StatusCode == HttpStatusCode.OK)
            {
                var mediaResponse = JsonConvert.DeserializeObject<InstaMediaListResponse>(json,
                    new InstaMediaListDataConverter());
                var moreAvailable = mediaResponse.MoreAvailable;
                var converter = ConvertersFabric.GetMediaListConverter(mediaResponse);
                var mediaList = converter.Convert();
                mediaList.Pages++;
                var nextId = mediaResponse.NextMaxId;
                while (moreAvailable && mediaList.Pages < maxPages)
                {
                    var result = await GetLikeFeedInternal(nextId);
                    if (!result.Succeeded)
                        return Result.Fail(result.Info, mediaList);
                    converter = ConvertersFabric.GetMediaListConverter(result.Value);
                    mediaList.AddRange(converter.Convert());
                    mediaList.Pages++;
                    nextId = mediaResponse.NextMaxId;
                    moreAvailable = result.Value.MoreAvailable;
                }
                return Result.Success(mediaList);
            }
            return Result.UnExpectedResponse<InstaMediaList>(response, json);
        }

        public async Task<IResult<InstaMediaListResponse>> GetLikeFeedInternal(string maxId = "")
        {
            var instaUri = UriCreator.GetUserLikeFeedUri(maxId);
            var request = HttpHelper.GetDefaultRequest(HttpMethod.Get, instaUri, _deviceInfo);
            var response = await _httpRequestProcessor.SendAsync(request);
            var json = await response.Content.ReadAsStringAsync();
            if (response.StatusCode != HttpStatusCode.OK)
                return Result.UnExpectedResponse<InstaMediaListResponse>(response, json);
            var mediaResponse = JsonConvert.DeserializeObject<InstaMediaListResponse>(json,
                new InstaMediaListDataConverter());
            return Result.Success(mediaResponse);
        }

        public async Task<IResult<InstaFriendshipStatus>> GetFriendshipStatusAsync(long userId)
        {
            ValidateUser();
            var userUri = UriCreator.GetUserFriendshipUri(userId);
            var request = HttpHelper.GetDefaultRequest(HttpMethod.Get, userUri, _deviceInfo);
            var response = await _httpRequestProcessor.SendAsync(request);
            var json = await response.Content.ReadAsStringAsync();
            if (response.StatusCode != HttpStatusCode.OK)
                return Result.UnExpectedResponse<InstaFriendshipStatus>(response, json);
            var friendshipStatusResponse = JsonConvert.DeserializeObject<InstaFriendshipStatusResponse>(json);
            var converter = ConvertersFabric.GetFriendShipStatusConverter(friendshipStatusResponse);
            return Result.Success(converter.Convert());
        }

        #endregion

        #region private part

        private void ValidateUser()
        {
            if (string.IsNullOrEmpty(_user.UserName) || string.IsNullOrEmpty(_user.Password))
                throw new ArgumentException("user name and password must be specified");
        }

        private void ValidateLoggedIn()
        {
            if (!IsUserAuthenticated) throw new ArgumentException("user must be authenticated");
        }

        private void ValidateRequestMessage()
        {
            if (_httpRequestProcessor.RequestMessage == null || _httpRequestProcessor.RequestMessage.IsEmpty())
                throw new ArgumentException("API request message null or empty");
        }

        private async Task<IResult<InstaFeedResponse>> GetUserFeedWithMaxIdAsync(string maxId)
        {
            if (!Uri.TryCreate(new Uri(InstaApiConstants.INSTAGRAM_URL), InstaApiConstants.TIMELINEFEED,
                out var instaUri))
                throw new Exception("Cant create search user URI");
            var userUriBuilder = new UriBuilder(instaUri) { Query = $"max_id={maxId}" };
            var request = HttpHelper.GetDefaultRequest(HttpMethod.Get, userUriBuilder.Uri, _deviceInfo);
            request.Properties.Add(new KeyValuePair<string, object>(InstaApiConstants.HEADER_PHONE_ID,
                _httpRequestProcessor.RequestMessage.phone_id));
            request.Properties.Add(new KeyValuePair<string, object>(InstaApiConstants.HEADER_TIMEZONE,
                InstaApiConstants.TIMEZONE_OFFSET.ToString()));
            var response = await _httpRequestProcessor.SendAsync(request);
            var json = await response.Content.ReadAsStringAsync();
            if (response.StatusCode == HttpStatusCode.OK)
            {
                var feedResponse = JsonConvert.DeserializeObject<InstaFeedResponse>(json,
                    new InstaFeedResponseDataConverter());
                return Result.Success(feedResponse);
            }
            return Result.UnExpectedResponse<InstaFeedResponse>(response, json);
        }

        private async Task<IResult<InstaRecentActivityResponse>> GetFollowingActivityWithMaxIdAsync(string maxId)
        {
            var uri = UriCreator.GetFollowingRecentActivityUri(maxId);
            var request = HttpHelper.GetDefaultRequest(HttpMethod.Get, uri, _deviceInfo);
            var response = await _httpRequestProcessor.SendAsync(request);
            var json = await response.Content.ReadAsStringAsync();
            if (response.StatusCode == HttpStatusCode.OK)
            {
                var followingActivity = JsonConvert.DeserializeObject<InstaRecentActivityResponse>(json,
                    new InstaRecentActivityConverter());
                return Result.Success(followingActivity);
            }
            return Result.UnExpectedResponse<InstaRecentActivityResponse>(response, json);
        }

        private async Task<IResult<InstaMediaListResponse>> GetUserMediaListWithMaxIdAsync(Uri instaUri)
        {
            var request = HttpHelper.GetDefaultRequest(HttpMethod.Get, instaUri, _deviceInfo);
            var response = await _httpRequestProcessor.SendAsync(request);
            var json = await response.Content.ReadAsStringAsync();
            if (response.StatusCode == HttpStatusCode.OK)
            {
                var mediaResponse = JsonConvert.DeserializeObject<InstaMediaListResponse>(json,
                    new InstaMediaListDataConverter());
                return Result.Success(mediaResponse);
            }
            return Result.Fail("", (InstaMediaListResponse)null);
        }

        private async Task<IResult<InstaUserListShortResponse>> GetUserListByURIAsync(Uri uri)
        {
            ValidateUser();
            try
            {
                if (!IsUserAuthenticated) throw new ArgumentException("user must be authenticated");
                var request = HttpHelper.GetDefaultRequest(HttpMethod.Get, uri, _deviceInfo);
                var response = await _httpRequestProcessor.SendAsync(request);
                var json = await response.Content.ReadAsStringAsync();
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    var instaUserListResponse = JsonConvert.DeserializeObject<InstaUserListShortResponse>(json);
                    if (!instaUserListResponse.IsOk()) Result.Fail("", (InstaUserListShortResponse)null);
                    return Result.Success(instaUserListResponse);
                }
                return Result.UnExpectedResponse<InstaUserListShortResponse>(response, json);
            }
            catch (Exception exception)
            {
                LogException(exception);
                return Result.Fail(exception.Message, (InstaUserListShortResponse)null);
            }
        }

        private async Task<IResult<InstaActivityFeed>> GetRecentActivityInternalAsync(Uri uri, int maxPages = 0)
        {
            ValidateLoggedIn();
            if (maxPages == 0) maxPages = int.MaxValue;

            var request = HttpHelper.GetDefaultRequest(HttpMethod.Get, uri, _deviceInfo);
            var response = await _httpRequestProcessor.SendAsync(request, HttpCompletionOption.ResponseContentRead);
            var activityFeed = new InstaActivityFeed();
            var json = await response.Content.ReadAsStringAsync();
            if (response.StatusCode != HttpStatusCode.OK)
                return Result.UnExpectedResponse<InstaActivityFeed>(response, json);
            var feedPage = JsonConvert.DeserializeObject<InstaRecentActivityResponse>(json,
                new InstaRecentActivityConverter());
            activityFeed.IsOwnActivity = feedPage.IsOwnActivity;
            var nextId = feedPage.NextMaxId;
            activityFeed.Items.AddRange(
                feedPage.Stories.Select(ConvertersFabric.GetSingleRecentActivityConverter)
                    .Select(converter => converter.Convert()));
            var pages = 1;
            while (!string.IsNullOrEmpty(nextId) && pages < maxPages)
            {
                var nextFollowingFeed = await GetFollowingActivityWithMaxIdAsync(nextId);
                if (!nextFollowingFeed.Succeeded)
                    return Result.Success($"Not all pages was downloaded: {nextFollowingFeed.Info.Message}",
                        activityFeed);
                nextId = nextFollowingFeed.Value.NextMaxId;
                activityFeed.Items.AddRange(
                    feedPage.Stories.Select(ConvertersFabric.GetSingleRecentActivityConverter)
                        .Select(converter => converter.Convert()));
                pages++;
            }
            return Result.Success(activityFeed);
        }

        private async Task<IResult<InstaMediaListResponse>> GetTagFeedWithMaxIdAsync(string tag, string nextId)
        {
            ValidateUser();
            ValidateLoggedIn();
            try
            {
                var instaUri = UriCreator.GetTagFeedUri(tag);
                instaUri = new UriBuilder(instaUri) { Query = $"max_id={nextId}" }.Uri;
                var request = HttpHelper.GetDefaultRequest(HttpMethod.Get, instaUri, _deviceInfo);
                var response = await _httpRequestProcessor.SendAsync(request);
                var json = await response.Content.ReadAsStringAsync();
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    var feedResponse = JsonConvert.DeserializeObject<InstaMediaListResponse>(json,
                        new InstaMediaListDataConverter());
                    return Result.Success(feedResponse);
                }
                return Result.UnExpectedResponse<InstaMediaListResponse>(response, json);
            }
            catch (Exception exception)
            {
                return Result.Fail(exception.Message, (InstaMediaListResponse)null);
            }
        }

        private async Task<IResult<InstaCommentListResponse>> GetCommentListWithMaxIdAsync(string mediaId,
            string nextId)
        {
            var commentsUri = UriCreator.GetMediaCommentsUri(mediaId);
            var commentsUriMaxId = new UriBuilder(commentsUri) { Query = $"max_id={nextId}" }.Uri;
            var request = HttpHelper.GetDefaultRequest(HttpMethod.Get, commentsUriMaxId, _deviceInfo);
            var response = await _httpRequestProcessor.SendAsync(request);
            var json = await response.Content.ReadAsStringAsync();
            if (response.StatusCode == HttpStatusCode.OK)
            {
                var comments = JsonConvert.DeserializeObject<InstaCommentListResponse>(json);
                return Result.Success(comments);
            }
            return Result.Fail("", (InstaCommentListResponse)null);
        }

        private async Task<IResult<InstaFriendshipStatus>> FollowUnfollowUserInternal(long userId, Uri instaUri)
        {
            ValidateUser();
            ValidateLoggedIn();
            try
            {
                var fields = new Dictionary<string, string>
                {
                    {"_uuid", _deviceInfo.DeviceGuid.ToString()},
                    {"_uid", _user.LoggedInUder.Pk},
                    {"_csrftoken", _user.CsrfToken},
                    {"user_id", userId.ToString()},
                    {"radio_type", "wifi-none"}
                };
                var request = HttpHelper.GetSignedRequest(HttpMethod.Post, instaUri, _deviceInfo, fields);
                var response = await _httpRequestProcessor.SendAsync(request);
                var json = await response.Content.ReadAsStringAsync();
                if (response.StatusCode == HttpStatusCode.OK && !string.IsNullOrEmpty(json))
                {
                    var friendshipStatus = JsonConvert.DeserializeObject<InstaFriendshipStatusResponse>(json,
                        new InstaFriendShipDataConverter());
                    var converter = ConvertersFabric.GetFriendShipStatusConverter(friendshipStatus);
                    return Result.Success(converter.Convert());
                }
                return Result.UnExpectedResponse<InstaFriendshipStatus>(response, json);
            }
            catch (Exception exception)
            {
                LogException(exception);
                return Result.Fail(exception.Message, (InstaFriendshipStatus)null);
            }
        }

        private async Task<IResult<InstaExploreFeedResponse>> GetExploreFeedAsync(string maxId)
        {
            try
            {
                var exploreUri = UriCreator.GetExploreUri(maxId);
                var request = HttpHelper.GetDefaultRequest(HttpMethod.Get, exploreUri, _deviceInfo);
                var response = await _httpRequestProcessor.SendAsync(request);
                var json = await response.Content.ReadAsStringAsync();
                if (response.StatusCode != HttpStatusCode.OK) return Result.Fail("", (InstaExploreFeedResponse)null);
                return Result.Success(
                    JsonConvert.DeserializeObject<InstaExploreFeedResponse>(json, new InstaExploreFeedDataConverter()));
            }
            catch (Exception exception)
            {
                LogException(exception);
                return Result.Fail(exception.Message, (InstaExploreFeedResponse)null);
            }
        }

        private void LogException(Exception exception)
        {
            _logger?.LogException(exception);
        }
        #endregion
    }
}