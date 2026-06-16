using Microsoft.AspNetCore.Identity.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using StateLand.Data;              // <-- PulsegisContext
using StateLand.Models.Auth;      // <-- LoginRequest
using StateLand.Models.Entities;
using StateLand.Models.OTP;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using static System.Net.WebRequestMethods;

namespace StateLand.Controllers
{
    [ApiController]
    [Route("api/auth")]
    public class AuthController : ControllerBase
    {
        private readonly PulsegisContext _context;
        private readonly IConfiguration _config;
        private readonly SmsAuthService _smsAuth;
        private readonly SmsApiSettings _settings;
        private readonly HttpClient _http;


        public AuthController(PulsegisContext context, IConfiguration config,
        SmsAuthService smsAuth,
        IOptions<SmsApiSettings> settings,
        HttpClient http)
        {
            _context = context;
            _config = config;
            _smsAuth = smsAuth;
            _settings = settings.Value;
            _http = http;
        }


        [HttpPost("send-registration-otp")]
        public async Task<IActionResult> SendOtp([FromBody] SendOtpRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Cnic))
                return BadRequest(new { message = "CNIC is required." });

            if (string.IsNullOrWhiteSpace(request.MobileNo))
                return BadRequest(new { message = "Mobile number is required." });

            // var normalizedMobile = _smsAuth.NormalizeMobile(request.MobileNo);

            if (string.IsNullOrEmpty(request.MobileNo))
                return BadRequest(new { message = "Invalid mobile number format." });
            var exists = await _context.ApplicantDetail
                    .AnyAsync(x => x.UserName == request.Cnic || x.MobileNo == request.MobileNo);
            if (exists)
            {
                bool cnicExists = await _context.ApplicantDetail
                    .AnyAsync(x => x.UserName == request.Cnic || x.UserName == request.Cnic);

                bool mobileExists = await _context.ApplicantDetail
                    .AnyAsync(x => x.MobileNo == request.MobileNo);

                var errors = new List<string>();

                if (cnicExists)
                    errors.Add("CNIC already exists.");

                if (mobileExists)
                    errors.Add("Mobile number already exists.");

                return BadRequest(new
                {
                    message = "Duplicate record found.",
                    errors
                });
            }
            try
            {
                var otp = RandomNumberGenerator.GetInt32(100000, 999999).ToString();

                OtpStore.Store[request.MobileNo] =
                    (otp, DateTime.UtcNow.AddMinutes(_settings.OtpExpiryMinutes));

                var jwt = await _smsAuth.GetJwtAsync();

                var payload = new
                {
                    instanceName = _settings.InstanceName,
                    transferNo = $"REG-{DateTime.UtcNow.Ticks}",
                    stage = "User Registration",
                    smsText = otp,
                    mobileNo = request.MobileNo,
                    smsType = "OTP"
                };

                _http.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", jwt);

                var content = new StringContent(
                    JsonSerializer.Serialize(payload),
                    Encoding.UTF8,
                    "application/json"
                );
                var response = await _http.PostAsync(
                    $"{_settings.BaseUrl}/api/secure/send-otp",
                    content);

                if (!response.IsSuccessStatusCode)
                {
                    return StatusCode(500, new
                    {
                        message = "Failed to send OTP",
                        detail = await response.Content.ReadAsStringAsync()
                    });
                }
                else
                {
                    return Ok(new { message = "OTP sent successfully" });
                }
                
            }
            catch (Exception ex)
            {
                Console.WriteLine("CRASH: " + ex.ToString());

                return StatusCode(500, new
                {
                    message = "Server crashed",
                    error = ex.Message
                });
            }
        }

        [HttpPost("send-reset-password-otp")]
        public async Task<IActionResult> SendResetPasswordOtp([FromBody] ResetPasswordOtpRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.CNIC))
                return BadRequest(new { message = "CNIC is required." });

            try
            {
                var user = request.IsManager ? await _context.Users.Where(x => x.Username == request.CNIC)
                    .Select(x => new
                    {
                        UserId = (int?)x.UserId,
                        CNIC = x.Username,
                        MobileNo = x.MobileNo,
                        IsActive = x.IsActive
                    })
                    .FirstOrDefaultAsync()
                    : await _context.ApplicantDetail
                    .Where(x => x.CNIC == request.CNIC)
                    .Select(x => new
                    {
                        UserId = (int?)null,
                        CNIC = x.CNIC,
                        MobileNo = x.MobileNo,
                        IsActive = x.IsActive
                    })
                    .FirstOrDefaultAsync();
                if (user == null || !user.IsActive)
                {
                    return NotFound(new
                    {
                        message = "User not found or inactive."
                    });
                }

                if (string.IsNullOrWhiteSpace(user.MobileNo))
                {
                    return BadRequest(new
                    {
                        message = "No mobile number found for this account."
                    });
                }
                var otp = RandomNumberGenerator.GetInt32(100000, 999999).ToString();
                OtpStore.Store[user.MobileNo] = (otp, DateTime.UtcNow.AddMinutes(_settings.OtpExpiryMinutes));

                var jwt = await _smsAuth.GetJwtAsync();
                var payload = new
                {
                    instanceName = _settings.InstanceName,
                    transferNo = $"RESET-{DateTime.UtcNow.Ticks}",
                    stage = "Reset Password",
                    smsText = otp,
                    mobileNo = user.MobileNo,
                    smsType = "OTP"
                };

                _http.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", jwt);

                var content = new StringContent(
                    JsonSerializer.Serialize(payload),
                    Encoding.UTF8,
                    "application/json"
                );
                var response = await _http.PostAsync(
                    $"{_settings.BaseUrl}/api/secure/send-otp",
                    content
                );
                if (!response.IsSuccessStatusCode)
                {
                    return StatusCode(500, new
                    {
                        message = "Failed to send OTP",
                        detail = await response.Content.ReadAsStringAsync()
                    });
                }
                var maskedMobile =  await _smsAuth.MaskMobile(user.MobileNo);
                if (request.IsManager)
                {
                    return Ok(new
                    {
                        message = "OTP sent successfully",
                        mobileNo = maskedMobile,
                        fullMobileNo = user.MobileNo,
                        userId = user.UserId 
                    });
                }

                return Ok(new
                {
                    message = "OTP sent successfully",
                    mobileNo = maskedMobile,        
                    fullMobileNo = user.MobileNo,   
                    CNIC = user.CNIC
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    message = "Server error",
                    error = ex.Message
                });
            }
        }


        // ================= VERIFY OTP =================
        [HttpPost("verify-registration-otp")]
        public IActionResult VerifyOtp([FromBody] VerifyOtpRequest request)
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            if (request == null || string.IsNullOrWhiteSpace(request.MobileNo))
                return BadRequest("Invalid request");

            if (OtpStore.Store == null)
                return StatusCode(500, "OTP service not initialized");

            if (!OtpStore.Store.TryGetValue(request.MobileNo, out var record))
                return BadRequest("OTP not found or expired");

            if (record.Expiry < DateTime.UtcNow)
            {
                OtpStore.Store.TryRemove(request.MobileNo, out _);
                return BadRequest("OTP expired");
            }

            if (!string.Equals(record.Otp, request.Otp, StringComparison.Ordinal))
                return BadRequest("Invalid OTP");

            OtpStore.Store.TryRemove(request.MobileNo, out _);
            return Ok(new { message = "OTP verified successfully" });
        }


        [HttpPost("create-user")]
        public IActionResult CreateUser(CreateUserRequest model)
        {

            var existing = _context.Users.FirstOrDefault(u => u.Username == model.Username);
            if (existing != null)
                return BadRequest("Username already exists.");

            string hashedPassword = BCrypt.Net.BCrypt.HashPassword(model.Password, workFactor: 10);

            var user = new User
            {
                Username = model.Username,
                PasswordHash = hashedPassword,
                FullName = model.FullName,
                RoleId = model.RoleId,
                IsActive = true,
                PassswordUpdated = false
            };

            _context.Users.Add(user);
            _context.SaveChanges();
            return Ok(new { message = "User created successfully." });
        }

        [HttpPost("reset-public-user-password")]
        public async Task<IActionResult> ResetPassword([FromBody] Models.Auth.ResetPasswordRequest model)
        {
            if (model == null)
                return BadRequest(new { message = "Invalid request payload." });

            if (string.IsNullOrWhiteSpace(model.CNIC) ||
                string.IsNullOrWhiteSpace(model.NewPassword))
            {
                return BadRequest(new { message = "Required fields are missing." });
            }

            if (model.NewPassword.Length < 8)
            {
                return BadRequest(new { message = "Password must be at least 8 characters long." });
            }

            try
            {
                var user = await _context.ApplicantDetail
                    .FirstOrDefaultAsync(u => u.CNIC == model.CNIC);

                if (user == null || !user.IsActive)
                {
                    return NotFound(new { message = "User not found or inactive." });
                }

                user.Password = BCrypt.Net.BCrypt.HashPassword(model.NewPassword, workFactor: 12);
                user.UpdatedAt = DateTime.UtcNow;
                _context.ApplicantDetail.Update(user);
                await _context.SaveChangesAsync();
                return Ok(new
                {
                    message = "Password reset successfully. Please login."
                });
            }
            catch (Exception)
            {
                return StatusCode(500, new
                {
                    message = "An unexpected error occurred."
                });
            }
        }

        [HttpPost("create-public-user")]
        public async Task<IActionResult> CreatePublicUser(ApplicantDetail model)
        {
            bool exists = await _context.ApplicantDetail.AnyAsync(u =>
                u.CNIC == model.CNIC ||
                u.UserName == model.CNIC || u.MobileNo == model.MobileNo);

            if (exists)
            {
                return BadRequest(new
                {
                    message = "User already exists."
                });
            }

            var strategy = _context.Database.CreateExecutionStrategy();

            try
            {
                await strategy.ExecuteAsync(async () =>
                {
                    await using var transaction =
                        await _context.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);

                    try
                    {
                        string hashedPassword =
                            BCrypt.Net.BCrypt.HashPassword(model.Password);

                        var applicant = new ApplicantDetail
                        {
                            UserName = model.CNIC,
                            Password = hashedPassword,
                            CNIC = model.CNIC,
                            MobileNo = model.MobileNo,
                            FullName = model.FullName,
                            DivisionId = model.DivisionId,
                            DistrictId = model.DistrictId,
                            TehsilId = model.TehsilId,
                            MauzaId = model.MauzaId,
                            IsActive = true
                        };

                        await _context.ApplicantDetail.AddAsync(applicant);
                        await _context.SaveChangesAsync();

                        await transaction.CommitAsync();
                    }
                    catch
                    {
                        await transaction.RollbackAsync();
                        throw;
                    }
                });

                return Ok(new
                {
                    message = "User created successfully."
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    message = ex.Message
                });
            }
        }

        [HttpPost("login")]
        public IActionResult Login(Models.Auth.LoginRequest model)
        {
            var user = _context.Users
                .FirstOrDefault(x => x.Username == model.Username && x.IsActive);

            if (user == null || !BCrypt.Net.BCrypt.Verify(model.Password, user.PasswordHash))
                return Unauthorized("Invalid credentials");

            // 🔐 Force password update on first login
            if (!user.PassswordUpdated)
            {
                return Ok(new
                {
                    requirePasswordUpdate = true,
                    userId = user.UserId,
                    message = "Please update your password first"
                });
            }

            var claims = new[]
            {
                new Claim("UserId", user.UserId.ToString()),
                new Claim("RoleId", user.RoleId.ToString()),
                new Claim(ClaimTypes.Role, user.RoleId.ToString())
            };

            var key = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(_config["Jwt:Key"])
            );

            var token = new JwtSecurityToken(
                issuer: _config["Jwt:Issuer"],
                audience: _config["Jwt:Audience"],
                claims: claims,
                expires: DateTime.UtcNow.AddHours(8),
                signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256)
            );

            return Ok(new
            {
                token = new JwtSecurityTokenHandler().WriteToken(token),
                roleId = user.RoleId,
                userName = user.FullName
            });
        }

        [HttpPost("login-public-user")]
        public IActionResult LoginPublicUser(Models.Auth.LoginRequest model)
        {
            var applicant = _context.ApplicantDetail
                .FirstOrDefault(x => x.UserName == model.Username && x.IsActive);

            if (applicant == null || !BCrypt.Net.BCrypt.Verify(model.Password, applicant.Password))
                return Unauthorized(new { message = "Invalid credentials" });

            var claims = new[]
            {
        new Claim("UserId", applicant.ApplicantId.ToString()),
        new Claim("RoleId", (-10).ToString()),
        new Claim(ClaimTypes.Role, (-10).ToString())
    };

            var key = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(_config["JwtForPublic:Key"])
            );

            var expiry = model.RememberMe
                ? DateTime.UtcNow.AddDays(30)
                : DateTime.UtcNow.AddHours(8);

            var token = new JwtSecurityToken(
                issuer: _config["JwtForPublic:Issuer"],
                audience: _config["JwtForPublic:Audience"],
                claims: claims,
                expires: expiry,
                signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256)
            );

            return Ok(new
            {
                token = new JwtSecurityTokenHandler().WriteToken(token),
                userId = applicant.ApplicantId,
                roleId = -10,
                userName = applicant.FullName
            });
        }

        public class UpdatePasswordRequest
        {
            public int UserId { get; set; }
            public string NewPassword { get; set; }
        }    


        [HttpPost("updatePassword")]
        public IActionResult UpdatePassword(UpdatePasswordRequest model)
        {
            var user = _context.Users.FirstOrDefault(u => u.UserId == model.UserId);

            if (user == null || !user.IsActive)
                return BadRequest("User not found");

            // 🔐 Hash new password
            user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(model.NewPassword, workFactor: 10);
            user.PassswordUpdated = true;
            user.UpdatedOn = DateTime.Now;

            _context.SaveChanges();

            return Ok(new
            {
                message = "Password updated successfully. Please login again."
            });
        }

    }
}
