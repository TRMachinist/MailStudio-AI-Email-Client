using System.Text;

namespace MailStudio.Regression;

static class EncodingFixtures
{
    public const string Text = "İmalat Şartı: paylaştığım parçaların ölçüsü, ığüşöç İĞÜŞÖÇ.";

    public static string Message(string charset, bool quotedPrintable)
    {
        // Get the fixture encoder directly: this must not register legacy encodings for the product.
        var encoding = CodePagesEncodingProvider.Instance.GetEncoding(charset) ?? Encoding.GetEncoding(charset);
        string html = $"<html><head><meta http-equiv=\"Content-Type\" content=\"text/html; charset={charset}\"></head><body><p>{Text}</p><p>ACME&copy; 2026; 10&sup1;, &ordm;</p></body></html>";
        var bytes = encoding.GetBytes(html);
        var body = quotedPrintable ? string.Concat(bytes.Select(b => $"={b:X2}")) : Convert.ToBase64String(bytes);
        return $"From: =?{charset}?B?{Convert.ToBase64String(encoding.GetBytes("Çağrı Işıl"))}?= <sender@example.test>\r\n" +
            $"Subject: =?{charset}?B?{Convert.ToBase64String(encoding.GetBytes("İmalat Şartı"))}?=\r\n" +
            $"MIME-Version: 1.0\r\nContent-Type: text/html; charset={charset}\r\n" +
            $"Content-Transfer-Encoding: {(quotedPrintable ? "quoted-printable" : "base64")}\r\n\r\n{body}\r\n";
    }
}
