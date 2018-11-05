﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Seq.Api.Model;
using Seq.Api.Model.Root;
using Seq.Api.Serialization;
using Tavis.UriTemplates;
using System.Threading;
using Seq.Api.Streams;
using System.Net.WebSockets;

namespace Seq.Api.Client
{
    public class SeqApiClient : IDisposable
    {
        readonly string _apiKey;

        // Future versions of Seq may not completely support v1 features, however
        // providing this as an Accept header will ensure what compatibility is available
        // can be utilised.
        const string SeqApiV6MediaType = "application/vnd.datalust.seq.v6+json";

        readonly HttpClient _httpClient;
        readonly CookieContainer _cookies = new CookieContainer();
        readonly JsonSerializer _serializer = JsonSerializer.Create(
            new JsonSerializerSettings
            {
                Converters = { new StringEnumConverter(), new LinkCollectionConverter() }
            });

        public SeqApiClient(string serverUrl, string apiKey = null, bool useDefaultCredentials = true)
        {
            ServerUrl = serverUrl ?? throw new ArgumentNullException(nameof(serverUrl));

            if (!string.IsNullOrEmpty(apiKey))
                _apiKey = apiKey;

            var handler = new HttpClientHandler
            {
                CookieContainer = _cookies,
                UseDefaultCredentials = useDefaultCredentials
            };

            var baseAddress = serverUrl;
            if (!baseAddress.EndsWith("/"))
                baseAddress += "/";

            _httpClient = new HttpClient(handler) { BaseAddress = new Uri(baseAddress) };
        }

        public string ServerUrl { get; }

        public HttpClient HttpClient => _httpClient;

        public Task<RootEntity> GetRootAsync(CancellationToken cancellationToken = default)
        {
            return HttpGetAsync<RootEntity>("api", cancellationToken);
        }

        public Task<TEntity> GetAsync<TEntity>(ILinked entity, string link, IDictionary<string, object> parameters = null, CancellationToken cancellationToken = default)
        {
            var linkUri = ResolveLink(entity, link, parameters);
            return HttpGetAsync<TEntity>(linkUri, cancellationToken);
        }

        public Task<string> GetStringAsync(ILinked entity, string link, IDictionary<string, object> parameters = null, CancellationToken cancellationToken = default)
        {
            var linkUri = ResolveLink(entity, link, parameters);
            return HttpGetStringAsync(linkUri, cancellationToken);
        }

        public Task<List<TEntity>> ListAsync<TEntity>(ILinked entity, string link, IDictionary<string, object> parameters = null, CancellationToken cancellationToken = default)
        {
            var linkUri = ResolveLink(entity, link, parameters);
            return HttpGetAsync<List<TEntity>>(linkUri, cancellationToken);
        }

        public async Task PostAsync<TEntity>(ILinked entity, string link, TEntity content, IDictionary<string, object> parameters = null, CancellationToken cancellationToken = default)
        {
            var linkUri = ResolveLink(entity, link, parameters);
            var request = new HttpRequestMessage(HttpMethod.Post, linkUri) { Content = MakeJsonContent(content) };
            var stream = await HttpSendAsync(request, cancellationToken).ConfigureAwait(false);
            new StreamReader(stream).ReadToEnd();
        }

        public async Task<TResponse> PostAsync<TEntity, TResponse>(ILinked entity, string link, TEntity content, IDictionary<string, object> parameters = null, CancellationToken cancellationToken = default)
        {
            var linkUri = ResolveLink(entity, link, parameters);
            var request = new HttpRequestMessage(HttpMethod.Post, linkUri) { Content = MakeJsonContent(content) };
            var stream = await HttpSendAsync(request, cancellationToken).ConfigureAwait(false);
            return _serializer.Deserialize<TResponse>(new JsonTextReader(new StreamReader(stream)));
        }

        public async Task<string> PostReadStringAsync<TEntity>(ILinked entity, string link, TEntity content, IDictionary<string, object> parameters = null, CancellationToken cancellationToken = default)
        {
            var linkUri = ResolveLink(entity, link, parameters);
            var request = new HttpRequestMessage(HttpMethod.Post, linkUri) { Content = MakeJsonContent(content) };
            var stream = await HttpSendAsync(request, cancellationToken).ConfigureAwait(false);
            return await new StreamReader(stream).ReadToEndAsync();
        }

        public async Task<Stream> PostReadStreamAsync<TEntity>(ILinked entity, string link, TEntity content, IDictionary<string, object> parameters = null, CancellationToken cancellationToken = default)
        {
            var linkUri = ResolveLink(entity, link, parameters);
            var request = new HttpRequestMessage(HttpMethod.Post, linkUri) { Content = MakeJsonContent(content) };
            return await HttpSendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        public async Task PutAsync<TEntity>(ILinked entity, string link, TEntity content, IDictionary<string, object> parameters = null, CancellationToken cancellationToken = default)
        {
            var linkUri = ResolveLink(entity, link, parameters);
            var request = new HttpRequestMessage(HttpMethod.Put, linkUri) { Content = MakeJsonContent(content) };
            var stream = await HttpSendAsync(request, cancellationToken).ConfigureAwait(false);
            new StreamReader(stream).ReadToEnd();
        }

        public async Task DeleteAsync<TEntity>(ILinked entity, string link, TEntity content, IDictionary<string, object> parameters = null, CancellationToken cancellationToken = default)
        {
            var linkUri = ResolveLink(entity, link, parameters);
            var request = new HttpRequestMessage(HttpMethod.Delete, linkUri) { Content = MakeJsonContent(content) };
            var stream = await HttpSendAsync(request, cancellationToken).ConfigureAwait(false);
            new StreamReader(stream).ReadToEnd();
        }

        public async Task<TResponse> DeleteAsync<TEntity, TResponse>(ILinked entity, string link, TEntity content, IDictionary<string, object> parameters = null, CancellationToken cancellationToken = default)
        {
            var linkUri = ResolveLink(entity, link, parameters);
            var request = new HttpRequestMessage(HttpMethod.Delete, linkUri) { Content = MakeJsonContent(content) };
            var stream = await HttpSendAsync(request, cancellationToken).ConfigureAwait(false);
            return _serializer.Deserialize<TResponse>(new JsonTextReader(new StreamReader(stream)));
        }

        public async Task<ObservableStream<TEntity>> StreamAsync<TEntity>(ILinked entity, string link, IDictionary<string, object> parameters = null, CancellationToken cancellationToken = default)
        {
            return await WebSocketStreamAsync(entity, link, parameters, reader => _serializer.Deserialize<TEntity>(new JsonTextReader(reader)), cancellationToken);
        }

        public async Task<ObservableStream<string>> StreamTextAsync(ILinked entity, string link, IDictionary<string, object> parameters = null, CancellationToken cancellationToken = default)
        {
            return await WebSocketStreamAsync(entity, link, parameters, reader => reader.ReadToEnd(), cancellationToken);
        }

        async Task<ObservableStream<T>> WebSocketStreamAsync<T>(ILinked entity, string link, IDictionary<string, object> parameters, Func<TextReader, T> deserialize, CancellationToken cancellationToken = default)
        {
            var linkUri = ResolveLink(entity, link, parameters);

            var socket = new ClientWebSocket();
            socket.Options.Cookies = _cookies;
            if (_apiKey != null)
                socket.Options.SetRequestHeader("X-Seq-ApiKey", _apiKey);

            await socket.ConnectAsync(new Uri(linkUri), cancellationToken);

            return new ObservableStream<T>(socket, deserialize);
        }

        async Task<T> HttpGetAsync<T>(string url, CancellationToken cancellationToken = default)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            var stream = await HttpSendAsync(request, cancellationToken).ConfigureAwait(false);
            return _serializer.Deserialize<T>(new JsonTextReader(new StreamReader(stream)));
        }

        async Task<string> HttpGetStringAsync(string url, CancellationToken cancellationToken = default)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            var stream = await HttpSendAsync(request, cancellationToken).ConfigureAwait(false);
            return await new StreamReader(stream).ReadToEndAsync();
        }

        async Task<Stream> HttpSendAsync(HttpRequestMessage request, CancellationToken cancellationToken = default)
        {
            if (_apiKey != null)
                request.Headers.Add("X-Seq-ApiKey", _apiKey);

            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(SeqApiV6MediaType));

            var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
                return stream;

            Dictionary<string, object> payload = null;
            try
            {
                payload = _serializer.Deserialize<Dictionary<string, object>>(new JsonTextReader(new StreamReader(stream)));
            }
            // ReSharper disable once EmptyGeneralCatchClause
            catch { }

            if (payload != null && payload.TryGetValue("Error", out var error) && error != null)
                throw new SeqApiException($"{(int)response.StatusCode} - {error}", response.StatusCode);

            throw new SeqApiException($"The Seq request failed ({(int)response.StatusCode}).", response.StatusCode);
        }

        HttpContent MakeJsonContent(object content)
        {
            var json = new StringWriter();
            _serializer.Serialize(json, content);
            return new StringContent(json.ToString(), Encoding.UTF8, "application/json");
        }

        static string ResolveLink(ILinked entity, string link, IDictionary<string, object> parameters = null)
        {
            Link linkItem;
            if (!entity.Links.TryGetValue(link, out linkItem))
                throw new NotSupportedException($"The requested link `{link}` isn't available on entity `{entity}`.");

            var expression = linkItem.GetUri();
            var template = new UriTemplate(expression);
            if (parameters != null)
            {
                var missing = parameters.Select(p => p.Key).Except(template.GetParameterNames()).ToArray();
                if (missing.Any())
                    throw new ArgumentException($"The URI template `{expression}` does not contain parameter: `{string.Join("`, `", missing)}`.");

                foreach (var parameter in parameters)
                {
                    var value = parameter.Value is DateTime time
                        ? time.ToString("O")
                        : parameter.Value;

                    template.SetParameter(parameter.Key, value);
                }
            }

            return template.Resolve();
        }

        public void Dispose()
        {
            _httpClient.Dispose();
        }
    }
}