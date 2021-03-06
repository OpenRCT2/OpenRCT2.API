using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenRCT2.API.Configuration;
using OpenRCT2.API.Extensions;
using OpenRCT2.DB.Abstractions;
using OpenRCT2.DB.Models;

namespace OpenRCT2.API.Services
{
    public class UserAuthenticationService
    {
        private readonly string _serverSalt;
        private readonly byte[] _authTokenSecret;
        private readonly IAuthTokenRepository _authTokenRepository;
        private readonly IUserRepository _userRepository;
        private readonly ILogger _logger;

        public UserAuthenticationService(
            IOptions<ApiConfig> configOptions,
            IAuthTokenRepository authTokenRepository,
            IUserRepository userRepository,
            ILogger<UserAuthenticationService> logger)
        {
            var config = configOptions.Value;
            _serverSalt = config.PasswordServerSalt;
            _authTokenSecret = Encoding.UTF8.GetBytes(config.AuthTokenSecret);
            _authTokenRepository = authTokenRepository;
            _userRepository = userRepository;
            _logger = logger;
        }

        public async Task<(User, AuthToken)> AuthenticateAsync(string name, string password)
        {
            _logger.LogInformation($"Authenticating user with name: '{name}'");
            var user = await _userRepository.GetUserFromNameAsync(name);
            if (user != null)
            {
                var givenHash = HashPassword(password, user.PasswordSalt);
                if (givenHash == user.PasswordHash)
                {
                    _logger.LogInformation($"Creating new token for user with name: {name}");
                    var authToken = CreateToken(user.Id);
                    await _authTokenRepository.InsertAsync(authToken);

                    // Update access time on user
                    user.LastAuthenticated = authToken.LastAccessed;
                    await _userRepository.UpdateUserAsync(user);

                    return (user, authToken);
                }
                else
                {
                    _logger.LogInformation($"Authentication failed (wrong password) for user with name: '{name}'");
                }
            }
            else
            {
                _logger.LogInformation($"Authentication failed, no such name: '{name}'");
            }
            return (null, null);
        }

        public async Task<User> AuthenticateWithTokenAsync(string token)
        {
            _logger.LogInformation($"Authenticating token");
            var authToken = await _authTokenRepository.GetFromTokenAsync(token);
            if (authToken != null)
            {
                if (ValidateToken(authToken))
                {
                    if (HasTokenExpired(authToken))
                    {
                        _logger.LogInformation($"Authentication token has expired");
                    }
                    else
                    {
                        _logger.LogInformation($"Authentication token is good");

                        // Update access time on token
                        authToken.LastAccessed = DateTime.UtcNow;
                        await _authTokenRepository.UpdateAsync(authToken);

                        // Return user
                        var user = await _userRepository.GetUserFromIdAsync(authToken.UserId);
                        if (user == null)
                        {
                            _logger.LogWarning($"Authentication token validated for non-existant user... removing token");
                            await _authTokenRepository.DeleteAsync(token);
                        }
                        else
                        {
                            // Update access time on user
                            user.LastAuthenticated = authToken.LastAccessed;
                            await _userRepository.UpdateUserAsync(user);
                            return user;
                        }
                    }
                }
                else
                {
                    _logger.LogInformation($"Authentication token was invalid");
                }
            }
            else
            {
                _logger.LogInformation($"Authentication token does not exist");
            }
            return null;
        }

        public async Task RevokeTokenAsync(string token)
        {
            await _authTokenRepository.DeleteAsync(token);
        }

        private AuthToken CreateToken(string userId)
        {
            // We need to truncate the created timestamp as the database will not keep the same precision
            // and it needs to be returned back in its original form otherwise the token will not validate.
            var created = DateTime.UtcNow;
            created = new DateTime(
                created.Year, created.Month, created.Day,
                created.Hour, created.Minute, created.Second, DateTimeKind.Utc);

            var authToken = new AuthToken();
            authToken.Id = Guid.NewGuid().ToString();
            authToken.UserId = userId;
            authToken.Created = created;
            authToken.LastAccessed = created;
            authToken.Token = GenerateToken(authToken.Id, authToken.UserId, authToken.Created);
            return authToken;
        }

        private static bool HasTokenExpired(AuthToken authToken)
        {
            // Expire token if it has not been used for over a month
            return DateTime.UtcNow > authToken.LastAccessed.AddMonths(1);
        }

        private bool ValidateToken(AuthToken authToken)
        {
            var expected = GenerateToken(authToken.Id, authToken.UserId, authToken.Created);
            if (authToken.Token == expected)
            {
                return true;
            }
            else
            {
                _logger.LogDebug($"ValidateToken, actual: {expected}");
                _logger.LogDebug($"ValidateToken, expected: {authToken.Token}");
                return false;
            }
        }

        private string GenerateToken(string id, string userId, DateTime created)
        {
            _logger.LogDebug($"GenerateToken({id}, {userId}, {created})");
            using (var hmac = new HMACSHA512(_authTokenSecret))
            {
                var ms = new MemoryStream();
                var bw = new BinaryWriter(ms);
                bw.Write(id);
                bw.Write(userId);
                bw.Write(created.ToBinary());
                var tokenBytes = hmac.ComputeHash(ms.ToArray());
                var result = tokenBytes.ToHexString();
                _logger.LogDebug($" -> {result}");
                return result;
            }
        }

        public string HashPassword(string password, string salt)
        {
            var input = _serverSalt + salt + password;
            using (var algorithm = SHA512.Create())
            {
                var hash = algorithm.ComputeHash(Encoding.ASCII.GetBytes(input));
                return hash.ToHexString();
            }
        }
    }
}
