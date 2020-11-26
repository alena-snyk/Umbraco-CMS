﻿using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Umbraco.Core;
using Umbraco.Core.BackOffice;
using Umbraco.Extensions;

namespace Umbraco.Web.Common.Security
{
    using Constants = Umbraco.Core.Constants;

    // TODO: There's potential to extract an interface for this for only what we use and put that in Core without aspnetcore refs, but we need to wait till were done with it since there's a bit to implement

    public class BackOfficeSignInManager : SignInManager<BackOfficeIdentityUser>
    {
        // borrowed from https://github.com/dotnet/aspnetcore/blob/master/src/Identity/Core/src/SignInManager.cs
        private const string LoginProviderKey = "LoginProvider";
        // borrowed from https://github.com/dotnet/aspnetcore/blob/master/src/Identity/Core/src/SignInManager.cs
        private const string XsrfKey = "XsrfId"; // TODO: See BackOfficeController.XsrfKey

        private BackOfficeUserManager _userManager;

        public BackOfficeSignInManager(
            BackOfficeUserManager userManager,
            IHttpContextAccessor contextAccessor,
            IUserClaimsPrincipalFactory<BackOfficeIdentityUser> claimsFactory,
            IOptions<IdentityOptions> optionsAccessor,
            ILogger<SignInManager<BackOfficeIdentityUser>> logger,
            IAuthenticationSchemeProvider schemes,
            IUserConfirmation<BackOfficeIdentityUser> confirmation)
            : base(userManager, contextAccessor, claimsFactory, optionsAccessor, logger, schemes, confirmation)
        {
            _userManager = userManager;
        }

        // TODO: Need to migrate more from Umbraco.Web.Security.BackOfficeSignInManager
        // Things like dealing with auto-linking, cookie options, and a ton of other stuff. Some might not need to be ported but it
        // will be a case by case basis.
        // Have a look into RefreshSignInAsync since we might be able to use this new functionality for auto-cookie renewal in our middleware, though
        // i suspect it's taken care of already.



        /// <inheritdoc />
        public override async Task<SignInResult> PasswordSignInAsync(BackOfficeIdentityUser user, string password, bool isPersistent, bool lockoutOnFailure)
        {
            // override to handle logging/events
            var result = await base.PasswordSignInAsync(user, password, isPersistent, lockoutOnFailure);
            return await HandleSignIn(user, user.UserName, result);
        }

        /// <inheritdoc />
        public override async Task<SignInResult> PasswordSignInAsync(string userName, string password, bool isPersistent, bool lockoutOnFailure)
        {
            // override to handle logging/events
            var user = await UserManager.FindByNameAsync(userName);
            if (user == null)
                return await HandleSignIn(null, userName, SignInResult.Failed);
            return await PasswordSignInAsync(user, password, isPersistent, lockoutOnFailure);
        }
        
        /// <inheritdoc />
        public override async Task<BackOfficeIdentityUser> GetTwoFactorAuthenticationUserAsync()
        {
            // borrowed from https://github.com/dotnet/aspnetcore/blob/master/src/Identity/Core/src/SignInManager.cs
            // replaced in order to use a custom auth type

            var info = await RetrieveTwoFactorInfoAsync();
            if (info == null)
            {
                return null;
            }
            return await UserManager.FindByIdAsync(info.UserId);
        }

        /// <inheritdoc />
        public override async Task<SignInResult> TwoFactorSignInAsync(string provider, string code, bool isPersistent, bool rememberClient)
        {
            // borrowed from https://github.com/dotnet/aspnetcore/blob/master/src/Identity/Core/src/SignInManager.cs#L552
            // replaced in order to use a custom auth type and to implement logging/events

            var twoFactorInfo = await RetrieveTwoFactorInfoAsync();
            if (twoFactorInfo == null || twoFactorInfo.UserId == null)
            {
                return SignInResult.Failed;
            }
            var user = await UserManager.FindByIdAsync(twoFactorInfo.UserId);
            if (user == null)
            {
                return SignInResult.Failed;
            }

            var error = await PreSignInCheck(user);
            if (error != null)
            {
                return error;
            }
            if (await UserManager.VerifyTwoFactorTokenAsync(user, provider, code))
            {
                await DoTwoFactorSignInAsync(user, twoFactorInfo, isPersistent, rememberClient);
                return await HandleSignIn(user, user?.UserName, SignInResult.Success);
            }
            // If the token is incorrect, record the failure which also may cause the user to be locked out
            await UserManager.AccessFailedAsync(user);
            return await HandleSignIn(user, user?.UserName, SignInResult.Failed);
        }

        
        /// <inheritdoc />
        public override bool IsSignedIn(ClaimsPrincipal principal)
        {
            // borrowed from https://github.com/dotnet/aspnetcore/blob/master/src/Identity/Core/src/SignInManager.cs#L126
            // replaced in order to use a custom auth type

            if (principal == null)
            {
                throw new ArgumentNullException(nameof(principal));
            }
            return principal?.Identities != null &&
                principal.Identities.Any(i => i.AuthenticationType == Constants.Security.BackOfficeAuthenticationType);
        }

        /// <inheritdoc />
        public override async Task RefreshSignInAsync(BackOfficeIdentityUser user)
        {
            // borrowed from https://github.com/dotnet/aspnetcore/blob/master/src/Identity/Core/src/SignInManager.cs#L126
            // replaced in order to use a custom auth type

            var auth = await Context.AuthenticateAsync(Constants.Security.BackOfficeAuthenticationType);
            IList<Claim> claims = Array.Empty<Claim>();

            var authenticationMethod = auth?.Principal?.FindFirst(ClaimTypes.AuthenticationMethod);
            var amr = auth?.Principal?.FindFirst("amr");

            if (authenticationMethod != null || amr != null)
            {
                claims = new List<Claim>();
                if (authenticationMethod != null)
                {
                    claims.Add(authenticationMethod);
                }
                if (amr != null)
                {
                    claims.Add(amr);
                }
            }

            await SignInWithClaimsAsync(user, auth?.Properties, claims);
        }

        /// <inheritdoc />
        public override async Task SignInWithClaimsAsync(BackOfficeIdentityUser user, AuthenticationProperties authenticationProperties, IEnumerable<Claim> additionalClaims)
        {
            // override to replace IdentityConstants.ApplicationScheme with Constants.Security.BackOfficeAuthenticationType
            // code taken from aspnetcore: https://github.com/dotnet/aspnetcore/blob/master/src/Identity/Core/src/SignInManager.cs
            // we also override to set the current HttpContext principal since this isn't done by default

            var userPrincipal = await CreateUserPrincipalAsync(user);
            foreach (var claim in additionalClaims)
                userPrincipal.Identities.First().AddClaim(claim);

            // FYI (just for informational purposes):
            // This calls an ext method will eventually reaches `IAuthenticationService.SignInAsync`
            // which then resolves the `IAuthenticationSignInHandler` for the current scheme
            // by calling `IAuthenticationHandlerProvider.GetHandlerAsync(context, scheme);`
            // which then calls `IAuthenticationSignInHandler.SignInAsync` = CookieAuthenticationHandler.HandleSignInAsync

            // Also note, that when the CookieAuthenticationHandler sign in is successful we handle that event within our
            // own ConfigureUmbracoBackOfficeCookieOptions which assigns the current HttpContext.User to the IPrincipal created

            // Also note, this method gets called when performing 2FA logins

            await Context.SignInAsync(Constants.Security.BackOfficeAuthenticationType,
                userPrincipal,
                authenticationProperties ?? new AuthenticationProperties());
        }

        /// <inheritdoc />
        public override async Task SignOutAsync()
        {
            // override to replace IdentityConstants.ApplicationScheme with Constants.Security.BackOfficeAuthenticationType
            // code taken from aspnetcore: https://github.com/dotnet/aspnetcore/blob/master/src/Identity/Core/src/SignInManager.cs

            await Context.SignOutAsync(Constants.Security.BackOfficeAuthenticationType);
            await Context.SignOutAsync(Constants.Security.BackOfficeExternalAuthenticationType);
            await Context.SignOutAsync(Constants.Security.BackOfficeTwoFactorAuthenticationType);
        }

        
        /// <inheritdoc />
        public override async Task<bool> IsTwoFactorClientRememberedAsync(BackOfficeIdentityUser user)
        {
            // borrowed from https://github.com/dotnet/aspnetcore/blob/master/src/Identity/Core/src/SignInManager.cs#L422
            // to replace the auth scheme

            var userId = await UserManager.GetUserIdAsync(user);
            var result = await Context.AuthenticateAsync(Constants.Security.BackOfficeTwoFactorRememberMeAuthenticationType);
            return (result?.Principal != null && result.Principal.FindFirstValue(ClaimTypes.Name) == userId);
        }

        
        /// <inheritdoc />
        public override async Task RememberTwoFactorClientAsync(BackOfficeIdentityUser user)
        {
            // borrowed from https://github.com/dotnet/aspnetcore/blob/master/src/Identity/Core/src/SignInManager.cs#L422
            // to replace the auth scheme

            var principal = await StoreRememberClient(user);
            await Context.SignInAsync(Constants.Security.BackOfficeTwoFactorRememberMeAuthenticationType,
                principal,
                new AuthenticationProperties { IsPersistent = true });
        }

        
        /// <inheritdoc />
        public override Task ForgetTwoFactorClientAsync()
        {
            // borrowed from https://github.com/dotnet/aspnetcore/blob/master/src/Identity/Core/src/SignInManager.cs#L422
            // to replace the auth scheme

            return Context.SignOutAsync(Constants.Security.BackOfficeTwoFactorRememberMeAuthenticationType);
        }

        
        /// <inheritdoc />
        public override async Task<SignInResult> TwoFactorRecoveryCodeSignInAsync(string recoveryCode)
        {
            // borrowed from https://github.com/dotnet/aspnetcore/blob/master/src/Identity/Core/src/SignInManager.cs#L422
            // to replace the auth scheme

            var twoFactorInfo = await RetrieveTwoFactorInfoAsync();
            if (twoFactorInfo == null || twoFactorInfo.UserId == null)
            {
                return SignInResult.Failed;
            }
            var user = await UserManager.FindByIdAsync(twoFactorInfo.UserId);
            if (user == null)
            {
                return SignInResult.Failed;
            }

            var result = await UserManager.RedeemTwoFactorRecoveryCodeAsync(user, recoveryCode);
            if (result.Succeeded)
            {
                await DoTwoFactorSignInAsync(user, twoFactorInfo, isPersistent: false, rememberClient: false);
                return SignInResult.Success;
            }

            // We don't protect against brute force attacks since codes are expected to be random.
            return SignInResult.Failed;
        }

        
        /// <inheritdoc />
        public override async Task<ExternalLoginInfo> GetExternalLoginInfoAsync(string expectedXsrf = null)
        {
            // borrowed from https://github.com/dotnet/aspnetcore/blob/master/src/Identity/Core/src/SignInManager.cs#L422
            // to replace the auth scheme

            var auth = await Context.AuthenticateAsync(Constants.Security.BackOfficeExternalAuthenticationType);
            var items = auth?.Properties?.Items;
            if (auth?.Principal == null || items == null || !items.ContainsKey(LoginProviderKey))
            {
                return null;
            }

            if (expectedXsrf != null)
            {
                if (!items.ContainsKey(XsrfKey))
                {
                    return null;
                }
                var userId = items[XsrfKey] as string;
                if (userId != expectedXsrf)
                {
                    return null;
                }
            }

            var providerKey = auth.Principal.FindFirstValue(ClaimTypes.NameIdentifier);
            var provider = items[LoginProviderKey] as string;
            if (providerKey == null || provider == null)
            {
                return null;
            }

            var providerDisplayName = (await GetExternalAuthenticationSchemesAsync()).FirstOrDefault(p => p.Name == provider)?.DisplayName ?? provider;
            return new ExternalLoginInfo(auth.Principal, provider, providerKey, providerDisplayName)
            {
                AuthenticationTokens = auth.Properties.GetTokens(),
                AuthenticationProperties = auth.Properties
            };
        }

        
        /// <inheritdoc />
        protected override async Task<SignInResult> SignInOrTwoFactorAsync(BackOfficeIdentityUser user, bool isPersistent, string loginProvider = null, bool bypassTwoFactor = false)
        {
            // borrowed from https://github.com/dotnet/aspnetcore/blob/master/src/Identity/Core/src/SignInManager.cs
            // to replace custom auth types

            if (!bypassTwoFactor && await IsTfaEnabled(user))
            {
                if (!await IsTwoFactorClientRememberedAsync(user))
                {
                    // Store the userId for use after two factor check
                    var userId = await UserManager.GetUserIdAsync(user);
                    await Context.SignInAsync(IdentityConstants.TwoFactorUserIdScheme, StoreTwoFactorInfo(userId, loginProvider));
                    return SignInResult.TwoFactorRequired;
                }
            }
            // Cleanup external cookie
            if (loginProvider != null)
            {
                await Context.SignOutAsync(Constants.Security.BackOfficeExternalAuthenticationType);
            }
            if (loginProvider == null)
            {
                await SignInWithClaimsAsync(user, isPersistent, new Claim[] { new Claim("amr", "pwd") });
            }
            else
            {
                await SignInAsync(user, isPersistent, loginProvider);
            }
            return SignInResult.Success;
        }

        /// <summary>
        /// Called on any login attempt to update the AccessFailedCount and to raise events
        /// </summary>
        /// <param name="user"></param>
        /// <param name="username"></param>
        /// <param name="result"></param>
        /// <returns></returns>
        private async Task<SignInResult> HandleSignIn(BackOfficeIdentityUser user, string username, SignInResult result)
        {
            // TODO: Here I believe we can do all (or most) of the usermanager event raising so that it is not in the AuthenticationController

            if (username.IsNullOrWhiteSpace())
            {
                username = "UNKNOWN"; // could happen in 2fa or something else weird
            }   

            if (result.Succeeded)
            {
                //track the last login date
                user.LastLoginDateUtc = DateTime.UtcNow;
                if (user.AccessFailedCount > 0)
                {
                    //we have successfully logged in, reset the AccessFailedCount
                    user.AccessFailedCount = 0;
                }   
                await UserManager.UpdateAsync(user);

                Logger.LogInformation("User: {UserName} logged in from IP address {IpAddress}", username, Context.Connection.RemoteIpAddress);
                if (user != null)
                {
                    _userManager.RaiseLoginSuccessEvent(user, user.Id);
                }   
            }
            else if (result.IsLockedOut)
            {
                _userManager.RaiseAccountLockedEvent(user, user.Id);
                Logger.LogInformation("Login attempt failed for username {UserName} from IP address {IpAddress}, the user is locked", username, Context.Connection.RemoteIpAddress);
            }
            else if (result.RequiresTwoFactor)
            {
                Logger.LogInformation("Login attempt requires verification for username {UserName} from IP address {IpAddress}", username, Context.Connection.RemoteIpAddress);
            }   
            else if (!result.Succeeded || result.IsNotAllowed)
            {
                Logger.LogInformation("Login attempt failed for username {UserName} from IP address {IpAddress}", username, Context.Connection.RemoteIpAddress);
            }
            else
            {
                throw new ArgumentOutOfRangeException();
            }

            return result;
        }

        // borrowed from https://github.com/dotnet/aspnetcore/blob/master/src/Identity/Core/src/SignInManager.cs#L782
        // since it's not public
        private async Task<bool> IsTfaEnabled(BackOfficeIdentityUser user)
            => UserManager.SupportsUserTwoFactor &&
            await UserManager.GetTwoFactorEnabledAsync(user) &&
            (await UserManager.GetValidTwoFactorProvidersAsync(user)).Count > 0;

        // borrowed from https://github.com/dotnet/aspnetcore/blob/master/src/Identity/Core/src/SignInManager.cs#L743
        // to replace custom auth types
        private ClaimsPrincipal StoreTwoFactorInfo(string userId, string loginProvider)
        {
            var identity = new ClaimsIdentity(Constants.Security.BackOfficeTwoFactorAuthenticationType);
            identity.AddClaim(new Claim(ClaimTypes.Name, userId));
            if (loginProvider != null)
            {
                identity.AddClaim(new Claim(ClaimTypes.AuthenticationMethod, loginProvider));
            }
            return new ClaimsPrincipal(identity);
        }

        // borrowed from https://github.com/dotnet/aspnetcore/blob/master/src/Identity/Core/src/SignInManager.cs
        // copy is required in order to use custom auth types
        private async Task<ClaimsPrincipal> StoreRememberClient(BackOfficeIdentityUser user)
        {
            var userId = await UserManager.GetUserIdAsync(user);
            var rememberBrowserIdentity = new ClaimsIdentity(Constants.Security.BackOfficeTwoFactorRememberMeAuthenticationType);
            rememberBrowserIdentity.AddClaim(new Claim(ClaimTypes.Name, userId));
            if (UserManager.SupportsUserSecurityStamp)
            {
                var stamp = await UserManager.GetSecurityStampAsync(user);
                rememberBrowserIdentity.AddClaim(new Claim(Options.ClaimsIdentity.SecurityStampClaimType, stamp));
            }
            return new ClaimsPrincipal(rememberBrowserIdentity);
        }

        // borrowed from https://github.com/dotnet/aspnetcore/blob/master/src/Identity/Core/src/SignInManager.cs
        // copy is required in order to use custom auth types
        private async Task DoTwoFactorSignInAsync(BackOfficeIdentityUser user, TwoFactorAuthenticationInfo twoFactorInfo, bool isPersistent, bool rememberClient)
        {
            // When token is verified correctly, clear the access failed count used for lockout
            await ResetLockout(user);

            var claims = new List<Claim>
            {
                new Claim("amr", "mfa")
            };

            // Cleanup external cookie
            if (twoFactorInfo.LoginProvider != null)
            {
                claims.Add(new Claim(ClaimTypes.AuthenticationMethod, twoFactorInfo.LoginProvider));
                await Context.SignOutAsync(Constants.Security.BackOfficeExternalAuthenticationType);
            }
            // Cleanup two factor user id cookie
            await Context.SignOutAsync(Constants.Security.BackOfficeTwoFactorAuthenticationType);
            if (rememberClient)
            {
                await RememberTwoFactorClientAsync(user);
            }
            await SignInWithClaimsAsync(user, isPersistent, claims);
        }

        // borrowed from https://github.com/dotnet/aspnetcore/blob/master/src/Identity/Core/src/SignInManager.cs
        // copy is required in order to use a custom auth type
        private async Task<TwoFactorAuthenticationInfo> RetrieveTwoFactorInfoAsync()
        {
            var result = await Context.AuthenticateAsync(Constants.Security.BackOfficeTwoFactorAuthenticationType);
            if (result?.Principal != null)
            {
                return new TwoFactorAuthenticationInfo
                {
                    UserId = result.Principal.FindFirstValue(ClaimTypes.Name),
                    LoginProvider = result.Principal.FindFirstValue(ClaimTypes.AuthenticationMethod)
                };
            }
            return null;
        }

        // borrowed from https://github.com/dotnet/aspnetcore/blob/master/src/Identity/Core/src/SignInManager.cs#L891
        private class TwoFactorAuthenticationInfo
        {
            public string UserId { get; set; }
            public string LoginProvider { get; set; }
        }
    }
}