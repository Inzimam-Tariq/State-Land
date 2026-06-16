namespace StateLand.Models.OTP
{

    public class ResetPasswordOtpRequest
    {
        public string CNIC { get; set; }
        public bool IsManager { get; set; }
    }

}
