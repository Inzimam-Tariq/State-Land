namespace StateLand.Models.OTP
{

    public class SmsApiSettings
    {
        public string BaseUrl { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
        public string InstanceName { get; set; }
        public int OtpExpiryMinutes { get; set; }
    }

}
