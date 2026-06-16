using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.VisualBasic;
using StateLand.Data;
using StateLand.Models.OTP;
using System.Data;
using System.Net.Http.Headers;
using System.Runtime;
using System.Text;
using System.Text.Json;
using static System.Net.WebRequestMethods;

namespace StateLand.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class GisController : ControllerBase
    {
        private readonly IConfiguration _config;
        private readonly PulsegisContext _dbContext;
        private readonly IWebHostEnvironment _env;
        private readonly SmsAuthService _smsAuth;
        private readonly SmsApiSettings _settings;
        private readonly HttpClient _http;

        public GisController(IConfiguration config, PulsegisContext dbContext, IWebHostEnvironment env,
        SmsAuthService smsAuth,
        IOptions<SmsApiSettings> settings,
        HttpClient http)
        {
            _config = config;
            _dbContext = dbContext;
            _env = env;
            _smsAuth = smsAuth;
            _settings = settings.Value;
            _http = http;
        }



        [HttpGet("token")]
        public async Task<IActionResult> GetToken()
        {
            var username = _config["GisAuth:Username"];
            var password = _config["GisAuth:Password"];
            var tokenUrl = _config["GisAuth:TokenUrl"]; // http://...
            var tokenUrlRef = _config["GisAuth:TokenUrlRef"]; // http://...

            try
            {
                // Create HttpClientHandler with SSL bypass for development
                var handler = new HttpClientHandler
                {
                    // ONLY FOR DEVELOPMENT - Bypass SSL certificate validation
                    ServerCertificateCustomValidationCallback =
                    HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
                };

                using var client = new HttpClient(handler);

                // The rest of your token generation code remains the same
                var content = new FormUrlEncodedContent(new[]
                {
            new KeyValuePair<string, string>("username", username),
            new KeyValuePair<string, string>("password", password),
            new KeyValuePair<string, string>("referer", tokenUrlRef),
            new KeyValuePair<string, string>("f", "json")
        });

                var response = await client.PostAsync(tokenUrl, content);

                if (!response.IsSuccessStatusCode)
                {
                    return BadRequest($"Failed to get token: {response.StatusCode}");
                }

                var result = await response.Content.ReadAsStringAsync();
                return Ok(result);
            }
            catch (Exception ex)
            {
                return BadRequest($"Error: {ex.Message}");
            }
        }


        public class StoredProcedureRequest
        {
            public string ProcedureName { get; set; } = string.Empty;
            public Dictionary<string, object?>? Parameters { get; set; }
        }

        [Authorize]
        [HttpPost("ExecuteStoredProcedure")]
        public async Task<IActionResult> ExecuteStoredProcedure(
        [FromBody] StoredProcedureRequest request)
        {
            try
            {
                if (request.Parameters != null && request.Parameters.ContainsKey("JsonPayload"))
                {
                    var rawJson = request.Parameters["JsonPayload"]?.ToString();

                    if (!string.IsNullOrEmpty(rawJson))
                    {
                        // Parse JSON
                        var jsonElement = JsonSerializer.Deserialize<JsonElement>(rawJson);

                        // Pretty print
                        var formattedJson = JsonSerializer.Serialize(
                            jsonElement,
                            new JsonSerializerOptions { WriteIndented = true }
                        );

                        // 👇 Debug output
                        Console.WriteLine("Formatted JSON:");
                        Console.WriteLine(formattedJson);
                    }
                }

                // ───────── Claims Extraction (Safe)
                var userIdClaim = User.FindFirst("UserId")?.Value;
                var roleIdClaim = User.FindFirst("RoleId")?.Value;

                if (!int.TryParse(userIdClaim, out int userId) ||
                    !int.TryParse(roleIdClaim, out int roleId))
                {
                    return Unauthorized("Invalid token claims");
                }

                // ───────── Request Validation
                if (request == null || string.IsNullOrWhiteSpace(request.ProcedureName))
                    return BadRequest("ProcedureName is required.");

                // ───────── Initialize Parameters (Critical Fix)
                request.Parameters ??= [];

                // ───────── Inject System Parameters
                request.Parameters["UserId"] = userId;

                // ───────── Execute
                var result = await ExecuteSP(request);

                return result;
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    Message = "Stored procedure execution failed",
                    Error = ex.Message,
                    request.ProcedureName
                });
            }
        }

        [HttpPost("send-status-sms")]
        [Authorize]
        public async Task<IActionResult> SendApplicationStatusSms([FromBody] ApplicationStatusSmsRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.CNIC))
                return BadRequest(new { message = "CNIC is required." });

            if (string.IsNullOrWhiteSpace(request.smsMessage))
                return BadRequest(new { message = "SMS message is required." });

            try
            {
                var applicant = await _dbContext.ApplicantDetail
                    .FirstOrDefaultAsync(x => x.CNIC == request.CNIC);

                if (applicant == null)
                    return NotFound(new { message = "Applicant not found for this CNIC." });

                if (string.IsNullOrWhiteSpace(applicant.MobileNo))
                    return BadRequest(new { message = "Mobile number not found for this applicant." });

                var jwt = await _smsAuth.GetJwtAsync();

                var payload = new
                {
                    instanceName = _settings.InstanceName, 
                    transferNo = $"APP-{applicant.CNIC}-{DateTime.UtcNow.Ticks}",
                    stage = "Application Result",
                    smsText = request.smsMessage,                    
                    //memberId = application.Id,
                    memberType = "Applicant",
                    mobileNo = applicant.MobileNo,        
                    smsType = "Regular"           
                };

                var httpRequest = new HttpRequestMessage(
                    HttpMethod.Post,
                    $"{_settings.BaseUrl}/api/secure/send"
                );

                httpRequest.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", jwt);

                httpRequest.Content = new StringContent(
                    JsonSerializer.Serialize(payload),
                    Encoding.UTF8,
                    "application/json"
                );
                var response = await _http.SendAsync(httpRequest);

                if (!response.IsSuccessStatusCode)
                {
                    return StatusCode(500, new
                    {
                        message = "Failed to send SMS",
                        detail = await response.Content.ReadAsStringAsync()
                    });
                }

                return Ok(new
                {
                    message = "Application status SMS sent successfully"
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

        [HttpPost("upload-images")]
        [Authorize]
        public async Task<IActionResult> UploadImages([FromForm] List<IFormFile> files)
        {
            try
            {
                if (files == null || files.Count == 0)
                    return BadRequest("No files uploaded.");

                var allowedExtensions = new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp" , ".pdf"};
                var folderPath = _config["FileStorage:ImagePath"];

                if (!Directory.Exists(folderPath))
                    Directory.CreateDirectory(folderPath);

                var uploadedFiles = new List<object>();

                foreach (var file in files)
                {
                    var extension = Path.GetExtension(file.FileName).ToLower();

                    if (!allowedExtensions.Contains(extension))
                        return BadRequest($"Invalid file type: {file.FileName}");

                    if (file.Length == 0)
                        return BadRequest($"Empty file: {file.FileName}");

                    if (file.Length > 5 * 1024 * 1024)
                        return BadRequest($"File too large: {file.FileName}");

                    // ✅ Generate unique filename
                    var safeFileName = Path.GetFileName(file.FileName);
                    var filePath = Path.Combine(folderPath, safeFileName);

                    try
                    {
                        using (var stream = new FileStream(filePath, FileMode.Create))
                        {
                            await file.CopyToAsync(stream);
                        }
                    }
                    catch (Exception ex)
                    {
                        throw new Exception($"Path: {filePath} | Error: {ex.Message}");
                    }

                    uploadedFiles.Add(new
                    {
                        FileName = safeFileName,
                        OriginalName = file.FileName,
                        Status = "Uploaded"
                    });
                }

                return Ok(new
                {
                    Message = "Files uploaded successfully",
                    Count = uploadedFiles.Count,
                    Files = uploadedFiles
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    Message = "Image upload failed",
                    Error = ex.Message
                });
            }
        }

        [HttpGet("get-image/{fileName}")]
        [Authorize]
        public IActionResult GetImage(string fileName)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(fileName))
                    return BadRequest("Filename is required.");

                var safeFileName = Path.GetFileName(fileName);
                var folderPath = _config["FileStorage:ImagePath"];
                var filePath = Path.Combine(folderPath, safeFileName);

                if (!System.IO.File.Exists(filePath))
                    return NotFound("File not found.");

                // ✅ Dynamic base URL (supports IIS + forwarded headers)
                var baseUrl = $"{Request.Scheme}://{Request.Host}{Request.PathBase}";

                var fileUrl = $"{baseUrl}/files/{safeFileName}";

                return Ok(new
                {
                    FileName = safeFileName,
                    Url = fileUrl
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    Message = "Failed to get image",
                    Error = ex.Message
                });
            }
        }

        //[HttpPost("upload-images")]
        //[Authorize]
        //public async Task<IActionResult> UploadImages([FromForm] List<IFormFile> files)
        //{
        //    try
        //    {
        //        if (files == null || files.Count == 0)
        //            return BadRequest("No files uploaded.");

        //        var allowedExtensions = new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp" };
        //        var folderPath = Path.Combine(_env.WebRootPath, "images");

        //        if (!Directory.Exists(folderPath))
        //            Directory.CreateDirectory(folderPath);

        //        var uploadedFiles = new List<object>();

        //        foreach (var file in files)
        //        {
        //            var safeFileName = Path.GetFileName(file.FileName);
        //            var extension = Path.GetExtension(file.FileName).ToLower();
        //            var filePath = Path.Combine(folderPath, safeFileName);
        //            if (!allowedExtensions.Contains(extension))
        //                continue;
        //            if (file.Length == 0 && !System.IO.File.Exists(filePath))
        //                continue;

        //            string status;
        //            if (System.IO.File.Exists(filePath) && file.Length == 0)
        //            {
        //                status = "Existing";
        //            }
        //            else
        //            {
        //                using (var stream = new FileStream(filePath, FileMode.Create))
        //                {
        //                    await file.CopyToAsync(stream);
        //                }

        //                status = System.IO.File.Exists(filePath) ? "Replaced" : "Uploaded";
        //            }

        //            var fileUrl = $"{Request.Scheme}://{Request.Host}/images/{safeFileName}";

        //            uploadedFiles.Add(new
        //            {
        //                FileName = safeFileName,
        //                Url = fileUrl,
        //                Status = status
        //            });
        //        }

        //        return Ok(new
        //        {
        //            Message = "Files processed successfully",
        //            Count = uploadedFiles.Count,
        //            Files = uploadedFiles
        //        });
        //    }
        //    catch (Exception ex)
        //    {
        //        return StatusCode(500, new
        //        {
        //            Message = "Image upload failed",
        //            Error = ex.Message
        //        });
        //    }
        //}

        int a = 0;
        //[HttpGet("get-image/{fileName}")]
        //[Authorize]
        //public IActionResult GetImage(string fileName)
        //{
        //    try
        //    {
        //        if (string.IsNullOrWhiteSpace(fileName))
        //            return BadRequest("Filename is required.");
        //        var safeFileName = Path.GetFileName(fileName);

        //        var folderPath = Path.Combine(_env.WebRootPath, "images");
        //        var filePath = Path.Combine(folderPath, safeFileName);

        //        if (!System.IO.File.Exists(filePath))
        //            return NotFound("File not found.");
        //        var contentType = fileName.ToLower() switch
        //        {
        //            var s when s.EndsWith(".jpg") || s.EndsWith(".jpeg") => "image/jpeg",
        //            var s when s.EndsWith(".png") => "image/png",
        //            var s when s.EndsWith(".gif") => "image/gif",
        //            var s when s.EndsWith(".webp") => "image/webp",
        //            _ => "application/octet-stream"
        //        };

        //        return PhysicalFile(filePath, contentType, safeFileName);
        //    }
        //    catch (Exception ex)
        //    {
        //        return StatusCode(500, new
        //        {
        //            Message = "Failed to get image",
        //            Error = ex.Message
        //        });
        //    }
        //}

        [AllowAnonymous]
        [HttpPost("ExecuteStoredProcedureAnonymous")]
        public async Task<IActionResult> PublicExecuteStoredProcedure(
        [FromBody] StoredProcedureRequest request)
        {
            var allowedProcedures = new List<string>
            {
                "i_GetHierarchyData",
                "TotalApplicationsStates",
                "sp_GetActivityConfig",
                "GetMauzaWiseLotsCount",
                "GetPublicApplicants_V1"
            };

            if (!allowedProcedures.Contains(request.ProcedureName))
            {
                return Unauthorized("Access to the requested procedure is denied.");
            }

            return await ExecuteSP(request);
        }


        private async Task<ActionResult> ExecuteSP(StoredProcedureRequest request)
        {
            try
            {
                var results = new List<object>();

                using var connection = _dbContext.Database.GetDbConnection();
                await connection.OpenAsync();

                using var command = connection.CreateCommand();
                command.CommandText = request.ProcedureName;
                command.CommandType = CommandType.StoredProcedure;
                command.CommandTimeout = 120;

                /* ✅ Ensure Parameters dictionary exists */
                if (request.Parameters == null)
                    request.Parameters = [];



                /* ✅ Dynamic parameters (JsonElement safe) */
                foreach (var p in request.Parameters)
                {
                    var param = command.CreateParameter();
                    param.ParameterName = p.Key.StartsWith("@") ? p.Key : "@" + p.Key;
                    param.Value = ConvertJsonElement(p.Value) ?? DBNull.Value;
                    command.Parameters.Add(param);
                }

                using var reader = await command.ExecuteReaderAsync();

                int resultSetIndex = 0;

                do
                {
                    var table = new List<Dictionary<string, object?>>();

                    while (await reader.ReadAsync())
                    {
                        var row = new Dictionary<string, object?>();

                        for (int i = 0; i < reader.FieldCount; i++)
                        {
                            row[reader.GetName(i)] =
                                reader.IsDBNull(i) ? null : reader.GetValue(i);
                        }

                        table.Add(row);
                    }

                    results.Add(new
                    {
                        ResultSetIndex = resultSetIndex,
                        Rows = table
                    });

                    resultSetIndex++;

                } while (await reader.NextResultAsync());

                /* ✅ Backward compatibility */
                if (results.Count == 1)
                    return Ok(((dynamic)results[0]).Rows);

                return Ok(results);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    Message = "Stored procedure execution failed",
                    Error = ex.Message,
                    request.ProcedureName
                });
            }
        }

        private static object? ConvertJsonElement(object? value)
        {
            if (value is not JsonElement json)
                return value;

            if (json.ValueKind == JsonValueKind.Null)
                return DBNull.Value;

            return json.ValueKind switch
            {
                JsonValueKind.String => json.GetString(),

                JsonValueKind.Number =>
                    json.TryGetInt64(out var l) ? l :
                    json.TryGetDecimal(out var d) ? d :
                    json.GetDouble(),

                JsonValueKind.True => true,
                JsonValueKind.False => false,

                _ => json.ToString()
            };
        }
    }
}
