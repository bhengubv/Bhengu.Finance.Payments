using System;
using System.Security.Cryptography;
using System.Text;

namespace Bhengu.Finance.Payments.PayShap.Utilities
{
    public static class PayShapSignatureHelper
    {
        public static string GenerateSignature(string payload, string secret)
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
            var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
            return Convert.ToHexString(hash).ToLower();
        }
    }
}
