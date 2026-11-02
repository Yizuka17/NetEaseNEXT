using System.IO.Compression;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace NetEaseNEXT.Api;

// Protocol adapted from @neteasecloudmusicapienhanced/api 4.41.1 (MIT).
// See third-party/netease-api/LICENSE. No JavaScript runtime is required.
public static class ApiEncryption
{
    private static readonly byte[] EapiKey = Encoding.ASCII.GetBytes("e82ckenh8dichen8");
    private static readonly byte[] XeapiKey = Convert.FromHexString("ab1d5a430f6bb04a3f01e81ddd72bd916d5ce591248ac128714806d7f8fb1b84");
    private const string SignKey = "mUHCwVNWJbunMqAHf5MImuirT6plvs6VSFW62MGHstFQxhBGdEoIhLItH3djc4+FB/OKty3+lL2rGeoFBpVe5g==";
    public static readonly JsonSerializerOptions JsonOptions = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static string Json(object value) => JsonSerializer.Serialize(value, JsonOptions);
    private static byte[] Encrypt(byte[] data, byte[] key, byte[]? iv = null)
    {
        using var aes = Aes.Create(); aes.Key = key;
        return iv == null ? aes.EncryptEcb(data, PaddingMode.PKCS7) : aes.EncryptCbc(data, iv, PaddingMode.PKCS7);
    }
    private static byte[] Decrypt(byte[] data, byte[] key)
    {
        using var aes = Aes.Create(); aes.Key = key; return aes.DecryptEcb(data, PaddingMode.PKCS7);
    }
    public static Dictionary<string, string> Eapi(string path, string json)
    {
        var digest = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes($"nobody{path}use{json}md5forencrypt"))).ToLowerInvariant();
        return new() { ["params"] = Convert.ToHexString(Encrypt(Encoding.UTF8.GetBytes($"{path}-36cd479b6b5-{json}-36cd479b6b5-{digest}"), EapiKey)) };
    }
    public static Dictionary<string, string> Weapi(string json, string? secret = null)
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        secret ??= new string(Enumerable.Range(0, 16).Select(_ => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)]).ToArray());
        var iv = Encoding.ASCII.GetBytes("0102030405060708");
        var first = Convert.ToBase64String(Encrypt(Encoding.UTF8.GetBytes(json), Encoding.ASCII.GetBytes("0CoJUm6Qyw8W8jud"), iv));
        using var rsa = RSA.Create();
        rsa.ImportFromPem("""
            -----BEGIN PUBLIC KEY-----
            MIGfMA0GCSqGSIb3DQEBAQUAA4GNADCBiQKBgQDgtQn2JZ34ZC28NWYpAUd98iZ37BUrX/aKzmFbt7clFSs6sXqHauqKWqdtLkF2KexO40H1YTX8z2lSgBBOAxLsvaklV8k4cBFK9snQXE9/DDaFt6Rr7iVZMldczhC0JNgTz+SHXT6CBHuX3e9SdB1Ua44oncaTWz7OBGLbCiK45wIDAQAB
            -----END PUBLIC KEY-----
            """);
        var key = rsa.ExportParameters(false);
        var number = new BigInteger(Encoding.ASCII.GetBytes(new string(secret.Reverse().ToArray())), true, true);
        var encryptedSecret = BigInteger.ModPow(number, new BigInteger(key.Exponent!, true, true), new BigInteger(key.Modulus!, true, true));
        return new() { ["params"] = Convert.ToBase64String(Encrypt(Encoding.ASCII.GetBytes(first), Encoding.ASCII.GetBytes(secret), iv)), ["encSecKey"] = Convert.ToHexString(encryptedSecret.ToByteArray(true, true)).ToLowerInvariant().PadLeft(256, '0') };
    }
    public static string Sign(string timestamp, string nonce) => Convert.ToBase64String(HMACSHA256.HashData(Encoding.ASCII.GetBytes(SignKey), Encoding.UTF8.GetBytes(timestamp + nonce)));
    private static JsonElement Parse(byte[] bytes) { using var document = JsonDocument.Parse(bytes); return document.RootElement.Clone(); }
    public static JsonElement DecodeKey(string encrypted) => Parse(Decrypt(Convert.FromBase64String(encrypted), XeapiKey));
    public static Dictionary<string, string> Xeapi(string content, JsonElement publicKey)
    {
        var dynamicKey = RandomNumberGenerator.GetBytes(16);
        var plaintext = Encoding.UTF8.GetBytes(Json(new { content, queryString = "e_r=true" }));
        var encrypted = Encrypt(plaintext, XeapiKey); var random = RandomNumberGenerator.GetBytes(16);
        for (var i = 0; i < encrypted.Length; i++) encrypted[i] ^= random[i & 15];
        var rotation = (random[0] & 15) % encrypted.Length;
        var transformed = random.Concat(encrypted.Skip(rotation)).Concat(encrypted.Take(rotation)).ToArray();
        var privateKey = new X25519PrivateKeyParameters(new SecureRandom());
        var ephemeral = privateKey.GeneratePublicKey().GetEncoded(); var shared = new byte[32];
        privateKey.GenerateSecret(new X25519PublicKeyParameters(Convert.FromBase64String(publicKey.GetProperty("publicKey").GetString()!)), shared, 0);
        var prk = HMACSHA256.HashData(new byte[32], shared);
        var aesKey = HMACSHA256.HashData(prk, ephemeral.Concat(new byte[] { 1 }).ToArray())[..16];
        var nonce = RandomNumberGenerator.GetBytes(12);
        var secret = Encoding.UTF8.GetBytes($"{Convert.ToBase64String(dynamicKey)}|android|{publicKey.GetProperty("sk").GetString()}");
        var sealedSecret = new byte[secret.Length]; var tag = new byte[16];
        using (var gcm = new AesGcm(aesKey, 16)) gcm.Encrypt(nonce, secret, sealedSecret, tag);
        return new()
        {
            ["C"] = Convert.ToBase64String(Encrypt(transformed, dynamicKey)).TrimEnd('=').Replace('+', '-').Replace('/', '_'),
            ["S"] = Convert.ToBase64String(ephemeral.Concat(nonce).Concat(sealedSecret).Concat(tag).ToArray()),
            ["R"] = Convert.ToBase64String(Encrypt(Encoding.UTF8.GetBytes(publicKey.GetProperty("version").ToString() + "|"), XeapiKey))
        };
    }
    public static JsonElement DecodeResponse(byte[] body)
    {
        try { return Parse(body); }
        catch (JsonException) { }
        var decoded = Decrypt(body, EapiKey);
        if (decoded.Length >= 2 && decoded[0] == 0x1f && decoded[1] == 0x8b)
        {
            using var gzip = new GZipStream(new MemoryStream(decoded), CompressionMode.Decompress);
            using var output = new MemoryStream(); gzip.CopyTo(output); decoded = output.ToArray();
        }
        return Parse(decoded);
    }
}
