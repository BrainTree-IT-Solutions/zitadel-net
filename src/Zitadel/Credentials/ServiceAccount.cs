﻿using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jose;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.OpenSsl;
using Org.BouncyCastle.Security;
using Zitadel.Authentication;

namespace Zitadel.Credentials
{
    /// <summary>
    /// <para>
    /// A ZITADEL <see cref="ServiceAccount"/> can be loaded from a json file
    /// and helps with authentication on a ZITADEL IAM.
    /// </para>
    /// <para>
    /// The mechanism is defined here:
    /// <a href="https://docs.zitadel.ch/docs/apis/openidoauth/grant-types#json-web-token-jwt-profile">JSON Web Token (JWT) Profile</a>.
    /// <a href="https://docs.zitadel.com/docs/apis/openidoauth/authn-methods#jwt-with-private-key">Create a JWT and sigh it with the private key</a>.
    /// </para>
    /// </summary>
    public record ServiceAccount
    {
        private static readonly HttpClient HttpClient = new();

        /// <summary>
        /// The key type.
        /// </summary>
        public const string Type = "serviceaccount";

        /// <summary>
        /// The user id associated with this service account.
        /// </summary>
        public string UserId { get; init; } = string.Empty;

        /// <summary>
        /// This is unique ID (on ZITADEL) of the key.
        /// </summary>
        public string KeyId { get; init; } = string.Empty;

        /// <summary>
        /// The private key generated by ZITADEL for this <see cref="ServiceAccount"/>.
        /// </summary>
        public string Key { get; init; } = string.Empty;

        /// <summary>
        /// Load a <see cref="ServiceAccount"/> from a file at a given (relative or absolute) path.
        /// </summary>
        /// <param name="pathToJson">The relative or absolute filepath to the json file.</param>
        /// <returns>The parsed <see cref="ServiceAccount"/>.</returns>
        /// <exception cref="FileNotFoundException">When the file does not exist.</exception>
        /// <exception cref="InvalidDataException">When the deserializer returns 'null'.</exception>
        /// <exception cref="JsonException">
        /// Thrown when the JSON is invalid,
        /// the <see cref="ServiceAccount"/> type is not compatible with the JSON,
        /// or when there is remaining data in the Stream.
        /// </exception>
        public static async Task<ServiceAccount> LoadFromJsonFileAsync(string pathToJson)
        {
            var path = Path.GetFullPath(
                Path.IsPathRooted(pathToJson)
                    ? pathToJson
                    : Path.Join(Directory.GetCurrentDirectory(), pathToJson));

            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"File not found: {path}.", path);
            }

            await using var stream = File.OpenRead(path);
            return await LoadFromJsonStreamAsync(stream);
        }

        /// <inheritdoc cref="LoadFromJsonFileAsync"/>
        public static ServiceAccount LoadFromJsonFile(string pathToJson) => LoadFromJsonFileAsync(pathToJson).Result;

        /// <summary>
        /// Load a <see cref="ServiceAccount"/> from a given stream (FileStream, MemoryStream, ...).
        /// </summary>
        /// <param name="stream">The stream to read the json from.</param>
        /// <returns>The parsed <see cref="ServiceAccount"/>.</returns>
        /// <exception cref="InvalidDataException">When the deserializer returns 'null'.</exception>
        /// <exception cref="JsonException">
        /// Thrown when the JSON is invalid,
        /// the <see cref="ServiceAccount"/> type is not compatible with the JSON,
        /// or when there is remaining data in the Stream.
        /// </exception>
        public static async Task<ServiceAccount> LoadFromJsonStreamAsync(Stream stream) =>
            await JsonSerializer.DeserializeAsync<ServiceAccount>(
                stream,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }) ??
            throw new InvalidDataException("The json file yielded a 'null' result for deserialization.");

        /// <inheritdoc cref="LoadFromJsonStreamAsync"/>
        public static ServiceAccount LoadFromJsonStream(Stream stream) => LoadFromJsonStreamAsync(stream).Result;

        /// <summary>
        /// Load a <see cref="ServiceAccount"/> from a string that contains json.
        /// </summary>
        /// <param name="json">Json string.</param>
        /// <returns>The parsed <see cref="ServiceAccount"/>.</returns>
        /// <exception cref="InvalidDataException">When the deserializer returns 'null'.</exception>
        /// <exception cref="JsonException">
        /// Thrown when the JSON is invalid,
        /// the <see cref="ServiceAccount"/> type is not compatible with the JSON,
        /// or when there is remaining data in the Stream.
        /// </exception>
        public static async Task<ServiceAccount> LoadFromJsonStringAsync(string json)
        {
            await using var memoryStream = new MemoryStream(Encoding.UTF8.GetBytes(json), 0, json.Length);
            return await LoadFromJsonStreamAsync(memoryStream);
        }

        /// <inheritdoc cref="LoadFromJsonStringAsync"/>
        public static ServiceAccount LoadFromJsonString(string json) => LoadFromJsonStringAsync(json).Result;

        /// <summary>
        /// Authenticate the given service account against the issuer in the options.
        /// As an example, the received token can be used to communicate with API applications or
        /// with the ZITADEL API itself.
        /// </summary>
        /// <param name="audience">The audience to authenticate against. Typically, this is a ZITADEL URL.</param>
        /// <param name="authOptions"><see cref="AuthOptions"/> that contain the parameters for the authentication process.</param>
        /// <returns>An opaque access token which can be used to communicate with relying parties.</returns>
        public async Task<string> AuthenticateAsync(string audience, AuthOptions? authOptions = null)
        {
            authOptions ??= new();
            var manager = new ConfigurationManager<OpenIdConnectConfiguration>(
                authOptions.DiscoveryEndpoint ?? DiscoveryEndpoint(audience),
                new OpenIdConnectConfigurationRetriever(),
                new HttpDocumentRetriever(HttpClient) { RequireHttps = authOptions.RequireHttps ?? true });

            var oidcConfig = await manager.GetConfigurationAsync();

            var jwt = await GetSignedJwtAsync(audience);
            var request = new HttpRequestMessage(HttpMethod.Post, oidcConfig.TokenEndpoint)
            {
                Content = new FormUrlEncodedContent(
                    new[]
                    {
                        new KeyValuePair<string?, string?>("grant_type", "urn:ietf:params:oauth:grant-type:jwt-bearer"),
                        new KeyValuePair<string?, string?>(
                            "assertion",
                            $"{jwt}"),
                        new KeyValuePair<string?, string?>("scope", authOptions.CreateOidcScopes()),
                    }),
            };

            var response = await HttpClient.SendAsync(request);

            try
            {
                var token = await response
                    .EnsureSuccessStatusCode()
                    .Content
                    .ReadFromJsonAsync<AccessTokenResponse>();
                return token?.AccessToken ?? throw new("Access token could not be parsed.");
            }
            catch (HttpRequestException e)
            {
                var err = await response.Content.ReadAsStringAsync();
                throw new HttpRequestException(err, e);
            }
        }

        /// <inheritdoc cref="AuthenticateAsync"/>
        public string Authenticate(string audience, AuthOptions? authOptions = null) =>
            AuthenticateAsync(audience, authOptions).Result;

        private static string DiscoveryEndpoint(string discoveryEndpoint) =>
            discoveryEndpoint.EndsWith(ZitadelDefaults.DiscoveryEndpointPath)
                ? discoveryEndpoint
                : discoveryEndpoint.TrimEnd('/') + ZitadelDefaults.DiscoveryEndpointPath;

        private async Task<string> GetSignedJwtAsync(string audience)
        {
            using var rsa = new RSACryptoServiceProvider();
            rsa.ImportParameters(await GetRsaParametersAsync());

            return JWT.Encode(
                new Dictionary<string, object>
                {
                    { "iss", UserId },
                    { "sub", UserId },
                    { "iat", DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeSeconds() },
                    { "exp", DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeSeconds() },
                    { "aud", audience },
                },
                rsa,
                JwsAlgorithm.RS256,
                new Dictionary<string, object>
                {
                    { "kid", KeyId },
                });
        }

        private async Task<RSAParameters> GetRsaParametersAsync()
        {
            var bytes = Encoding.UTF8.GetBytes(Key);
            await using var ms = new MemoryStream(bytes);
            using var sr = new StreamReader(ms);
            var pemReader = new PemReader(sr);

            if (pemReader.ReadObject() is not AsymmetricCipherKeyPair keyPair)
            {
                throw new("RSA Keypair could not be read.");
            }

            return DotNetUtilities.ToRSAParameters(keyPair.Private as RsaPrivateCrtKeyParameters);
        }

        private record AccessTokenResponse
        {
            [JsonPropertyName("access_token")]
            public string AccessToken { get; init; } = string.Empty;
        }

        /// <summary>
        /// Options for the authentication with a <see cref="ServiceAccount"/>.
        /// </summary>
        public record AuthOptions
        {
            /// <summary>
            /// Scope that can be added to signal that the ZITADEL API needs to be in the audience of the token.
            /// This replaces the need for the "project id" of the ZITADEL API project to be present.
            /// </summary>
            public const string ApiAccessScope = "urn:zitadel:iam:org:project:id:zitadel:aud";

            /// <summary>
            /// If set, the requested access token from ZITADEL will include the "ZITADEL API" project
            /// in its audience. The returned token will be able to access the API on the service accounts
            /// behalf.
            /// </summary>
            public bool ApiAccess { get; init; }

            /// <summary>
            /// If set, overwrites the discovery endpoint for the audience in the authentication.
            /// This may be used, if the discovery endpoint is not on the well-known url
            /// of the endpoint.
            /// </summary>
            public string? DiscoveryEndpoint { get; init; }

            /// <summary>
            /// Requires Https secure channel for sending requests. This is turned ON by default for security reasons. It is RECOMMENDED that you do not allow retrieval from http addresses by default.
            /// </summary>
            public bool? RequireHttps { get; init; }

            /// <summary>
            /// Set a list of roles that must be attached to this service account to be
            /// successfully authenticated. Translates to the role scope ("urn:zitadel:iam:org:project:role:{Role}").
            /// </summary>
            public IList<string> RequiredRoles { get; init; } = new List<string>();

            /// <summary>
            /// Set a list of audiences that are attached to the returned access token.
            /// Translates to the additional audience scope ("urn:zitadel:iam:org:project:id:{ProjectId}:aud").
            /// </summary>
            public IList<string> ProjectAudiences { get; init; } = new List<string>();

            /// <summary>
            /// List of arbitrary additional scopes that are concatenated into the scope.
            /// </summary>
            public IList<string> AdditionalScopes { get; init; } = new List<string>();

            internal string CreateOidcScopes() =>
                string.Join(
                    ' ',
                    new[]
                        {
                            "openid",
                            ApiAccess
                                ? ApiAccessScope
                                : string.Empty,
                        }
                        .Union(AdditionalScopes)
                        .Union(ProjectAudiences.Select(p => $"urn:zitadel:iam:org:project:id:{p}:aud"))
                        .Union(RequiredRoles.Select(r => $"urn:zitadel:iam:org:project:role:{r}"))
                        .Where(s => !string.IsNullOrWhiteSpace(s)));
        }
    }
}
