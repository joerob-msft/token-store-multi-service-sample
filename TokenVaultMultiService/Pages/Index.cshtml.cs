﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.IdentityModel.Tokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using System.Threading;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authentication;
using System.Net.Http.Headers;
using Microsoft.Azure.Services.AppAuthentication;
using System.Text;
using Dropbox.Api;
using Microsoft.Graph;
using System.Security.Claims;

namespace TokenVaultMultiService.Pages
{
    public class IndexModel : PageModel
    {
        private readonly IConfiguration _configuration;
        private static readonly HttpClient _httpClient = new HttpClient();

        public IndexModel(IConfiguration configuration)
        {
            this._configuration = configuration;
        }

        public async Task OnGetAsync()
        {
            // Check if user is authenticated
            if (this.User.Identity.IsAuthenticated)
            {
                this.ViewData["loggedIn"] = true;
                this.ViewData["userName"] = this.User.FindFirst("name").Value;
                // TODO: can't use nameidentifier b/c Token Vault doesn't support underscores in names, and nameid can have underscores
                //var nameId = this.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                var objectId = this.User.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier").Value;

                // Get an API token to access Token Vault
                var azureServiceTokenProvider = new AzureServiceTokenProvider();
                var tokenVaultApiToken = await azureServiceTokenProvider.GetAccessTokenAsync("https://tokenvault.azure.net");
                var tokenVaultUrl = this._configuration["TokenVaultUrl"];

                // Get Token Vault token resource for Dropbox for this user (and create it if it doesn't exist)
                var tokenVaultDropboxToken = await EnsureTokenVaultTokenResourceAsync(tokenVaultUrl, "dropbox", objectId, tokenVaultApiToken);

                // Get Dropbox status from token resource and set in view data
                var isDropboxConnected = tokenVaultDropboxToken.IsStatusOk();
                this.ViewData["isDropboxConnected"] = isDropboxConnected;

                // If connected, get data from Dropbox and set in view data
                if (isDropboxConnected)
                {
                    this.ViewData["dropboxFileList"] = await GetDropboxDocumentsAsync(tokenVaultDropboxToken.value.accessToken);
                }
                // Otherwise, set Dropbox login URI in view data
                else
                {
                    var redirectUrl = GetPostLoginRedirectUrl("dropbox", objectId);
                    this.ViewData["dropboxLoginUrl"] = $"{tokenVaultDropboxToken.loginUri}?PostLoginRedirectUrl={Uri.EscapeDataString(redirectUrl)}";
                }



                // Get Token Vault token resource for Graph for this user (and create it if it doesn't exist)
                var tokenVaultGraphToken = await EnsureTokenVaultTokenResourceAsync(tokenVaultUrl, "graph", objectId, tokenVaultApiToken);

                // Get Graph status from token resource and set in view data
                var isGraphConnected = tokenVaultGraphToken.IsStatusOk();
                this.ViewData["isGraphConnected"] = isGraphConnected;

                // If connected, get data from Graph and set in view data
                if (isGraphConnected)
                {
                    this.ViewData["graphFileList"] = await GetGraphDocumentsAsync(tokenVaultGraphToken.value.accessToken);
                }
                // Otherwise, set Graph login URI in view data
                else
                {
                    var redirectUrl = GetPostLoginRedirectUrl("graph", objectId);
                    this.ViewData["graphLoginUrl"] = $"{tokenVaultGraphToken.loginUri}?PostLoginRedirectUrl={Uri.EscapeDataString(redirectUrl)}";
                }



                // Associate token name with this session, so that PostLoginRedirect can verify where the login request originated
                // TODO: session could expire... maybe move this to the login endpoint
                this.HttpContext.Session.SetString("tvId", objectId);
            }
            else
            {
                this.ViewData["loggedIn"] = false;
            }
        }

        #region Token Vault API methods

        private async Task<Models.TokenVaultToken> EnsureTokenVaultTokenResourceAsync(string tokenVaultUrl, string serviceId, string tokenId, string tokenVaultApiToken)
        {
            var retrievedToken = await GetTokenVaultTokenResourceAsync(tokenVaultUrl, serviceId, tokenId, tokenVaultApiToken);
            if (retrievedToken != null)
            {
                return retrievedToken;
            }

            return await CreateTokenVaultTokenResourceAsync(tokenVaultUrl, serviceId, tokenId, tokenVaultApiToken);
        }

        private async Task<Models.TokenVaultToken> CreateTokenVaultTokenResourceAsync(string tokenVaultUrl, string serviceId, string tokenId, string tokenVaultApiToken)
        {
            var uriBuilder = new UriBuilder(tokenVaultUrl);
            uriBuilder.Path = $"/services/{serviceId}/tokens/{tokenId}";
            var request = new HttpRequestMessage(HttpMethod.Put, uriBuilder.Uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenVaultApiToken);
            // TODO: might want a strongly-typed object
            var requestContent = JObject.FromObject(new
            {
                name = tokenId,
                displayName = tokenId
            });
            request.Content = new StringContent(requestContent.ToString(), Encoding.UTF8, "application/json");

            // TODO: need error handling on this request
            var response = await _httpClient.SendAsync(request);
            var responseStr = await response.Content.ReadAsStringAsync();
            var tokenVaultToken = JsonConvert.DeserializeObject<Models.TokenVaultToken>(responseStr);

            return tokenVaultToken;
        }

        private async Task<Models.TokenVaultToken> GetTokenVaultTokenResourceAsync(string tokenVaultUrl, string serviceId, string tokenId, string tokenVaultApiToken)
        {
            var uriBuilder = new UriBuilder(tokenVaultUrl);
            uriBuilder.Path = $"/services/{serviceId}/tokens/{tokenId}";
            var request = new HttpRequestMessage(HttpMethod.Get, uriBuilder.Uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenVaultApiToken);

            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var responseStr = await response.Content.ReadAsStringAsync();
            var tokenVaultToken = JsonConvert.DeserializeObject<Models.TokenVaultToken>(responseStr);

            return tokenVaultToken;
        }

        #endregion

        #region Service APIs

        private async Task<IEnumerable<string>> GetDropboxDocumentsAsync(string token)
        {
            // Ensure token isn't empty
            if (string.IsNullOrEmpty(token))
            {
                return Enumerable.Empty<string>();
            }

            // Create DropboxClient and get file names
            using (var dbx = new DropboxClient(token))
            {
                var files = await dbx.Files.ListFolderAsync(string.Empty);
                var fileNames = files.Entries.Select(file => file.Name);
                return fileNames;
            }
        }

        private async Task<IEnumerable<string>> GetGraphDocumentsAsync(string token)
        {
            // Ensure token isn't empty
            if (string.IsNullOrEmpty(token))
            {
                return Enumerable.Empty<string>();
            }

            // Create GraphServiceClient and get file names
            var graphClient = new GraphServiceClient(new DelegateAuthenticationProvider((requestMessage) =>
            {
                requestMessage.Headers.Authorization = new AuthenticationHeaderValue("bearer", token);
                return Task.CompletedTask;
            }));
            var driveItems = await graphClient.Me.Drive.Root.Children.Request().GetAsync();
            var driveItemNames = driveItems.Select(driveItem => driveItem.Name);
            return driveItemNames;
        }

        #endregion

        #region Helper methods

        // Constructs the post-login redirect URL that we append to Token Vault login URLs
        private string GetPostLoginRedirectUrl(string serviceId, string tokenId)
        {
            var uriBuilder = new UriBuilder("https", this.Request.Host.Host, this.Request.Host.Port.GetValueOrDefault(-1), "postlogin");
            uriBuilder.Query = $"serviceId={serviceId}&tokenId={tokenId}";
            return uriBuilder.Uri.ToString();
        }

        #endregion
    }
}
