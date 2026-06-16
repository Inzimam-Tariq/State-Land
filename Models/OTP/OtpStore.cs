
using System.Collections.Concurrent;

namespace StateLand.Models.OTP
{
    public static class OtpStore
    {
        public static readonly ConcurrentDictionary<string, (string Otp, DateTime Expiry)>
            Store = new();
    }
}
