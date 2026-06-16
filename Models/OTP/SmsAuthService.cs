using Microsoft.Extensions.Options;

using System.Net.Http.Headers;
using System.Reflection;
using System.Text;


namespace StateLand.Models.OTP
{

    public class SmsAuthService
    {
        private readonly HttpClient _http;
        private readonly SmsApiSettings _settings;

        private string _cachedToken;
        private DateTime _tokenExpiry;

        public SmsAuthService(HttpClient http, IOptions<SmsApiSettings> settings)
        {
            _http = http;
            _settings = settings.Value;
        }


        public async Task<string> GetJwtAsync()
        {
            if (!string.IsNullOrEmpty(_cachedToken) && _tokenExpiry > DateTime.UtcNow)
                return _cachedToken;

            if (string.IsNullOrWhiteSpace(_settings.BaseUrl))
                throw new Exception("SMS BaseUrl is missing");

            var content = new FormUrlEncodedContent(new Dictionary<string, string>
    {
        { "username", _settings.Username },
        { "password", _settings.Password }
    });

            var loginUrl = $"{_settings.BaseUrl.TrimEnd('/')}/api/auth/login";

            var response = await _http.PostAsync(loginUrl, content);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                throw new Exception($"SMS LOGIN FAILED | {response.StatusCode} | {body}");

            _cachedToken = body;
            _tokenExpiry = DateTime.UtcNow.AddMinutes(45);

            return _cachedToken;
        }
        public async Task<string> MaskMobile(string mobile)
        {
            if (mobile.Length < 11) return mobile;

            return mobile.Substring(0, 3) + "*****" + mobile.Substring(8);
        }

    }
}
