namespace StateLand.Models.Auth
{
    public class ResetPasswordRequest
    {
        public string CNIC { get; set; }
        public string NewPassword { get; set; }
    }
}
