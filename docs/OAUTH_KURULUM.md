# MailStudio — OAuth uygulama kimliği alma

Microsoft ve Google, bir masaüstü programının tarayıcı üzerinden oturum açtırabilmesi için o programın
bir kez uygulama kaydı oluşturulmasını ister. Aynı istemci kimliği, kayıtlı hesap türleri ve kuruluş izinleri uygunsa diğer bilgisayarda da kullanılabilir; o bilgisayarda kullanıcı yeniden oturum açar.

---

## 1) Microsoft (Outlook.com, Hotmail, Microsoft 365)

1. https://entra.microsoft.com adresine gir (kişisel hesapla da girilebilir; ilk seferde ücretsiz bir dizin oluşturmanı isteyebilir).
2. **Uygulamalar → Uygulama kayıtları → Yeni kayıt**
   - Ad: `MailStudio`
   - Desteklenen hesap türleri: **Herhangi bir kuruluş dizinindeki hesaplar ve kişisel Microsoft hesapları**
   - Yeniden yönlendirme URI: platform **Ortak istemci/yerel (mobil ve masaüstü)**, adres `http://localhost`
3. **Kimlik doğrulama** sayfasında **Genel istemci akışlarına izin ver → Evet**, kaydet.
4. **API izinleri → İzin ekle → Kuruluşumun kullandığı API'ler → Office 365 Exchange Online → Temsilci izinleri** üzerinden şu izinleri ekle:
   - `IMAP.AccessAsUser.All`
   - `SMTP.Send`
   Uygulama oturum açarken ayrıca `offline_access` kapsamını ister. Kodun istediği kaynaklar `https://outlook.office.com/IMAP.AccessAsUser.All` ve `https://outlook.office.com/SMTP.Send` şeklindedir. Bunlar Microsoft Graph `Mail.Read`/`Mail.Send` izinleriyle aynı değildir. [Microsoft'un protokol OAuth belgesi](https://learn.microsoft.com/en-us/exchange/client-developer/legacy-protocols/how-to-authenticate-an-imap-pop-smtp-application-by-using-oauth).
5. **Genel bakış** sayfasındaki **Uygulama (istemci) kimliği** değerini kopyala.
6. MailStudio → Klasör → Hesap Ekle → "Outlook.com / Hotmail" veya "Microsoft 365" → **OAuth ayarları** → kimliği yapıştır → **Oturum Aç ve Kaydet**.

**Microsoft 365 (şirket hesabı) notları**
- Şirket, kullanıcıların üçüncü parti uygulamalara onay vermesini kapatmış olabilir; o durumda "yönetici onayı gerekli" uyarısı çıkar ve IT onay vermelidir.
- Posta kutusunda IMAP ve "SMTP AUTH" açık olmalıdır. Kapalıysa IT şu komutla açabilir:
  `Set-CASMailbox -Identity kullanici@sirket.com -ImapEnabled $true -SmtpClientAuthenticationDisabled $false`

---

## 2) Google (Gmail) — isteğe bağlı

Gmail için en kolay yol **uygulama şifresi**dir (myaccount.google.com/apppasswords). OAuth istersen:

1. https://console.cloud.google.com → yeni proje: `MailStudio`
2. **API'ler ve Hizmetler → Kitaplık → Gmail API → Etkinleştir**
3. **OAuth izin ekranı**: Harici, uygulama adı MailStudio; **Test kullanıcıları** bölümüne kendi Gmail adresini ekle.
4. **Kimlik bilgileri → Kimlik bilgisi oluştur → OAuth istemci kimliği → Masaüstü uygulaması**
5. Çıkan **İstemci kimliği** ve **İstemci gizli anahtarı** değerlerini MailStudio'daki OAuth ayarlarına yapıştır.
6. Hesap Ekle → "Gmail (Google ile oturum aç)" → **Oturum Aç ve Kaydet**.

Test modundaki Google uygulamalarında oturum 7 günde bir yenilenmek isteyebilir; uygulama bunu tarayıcıyı açarak ister.

---

Jetonlar `%AppData%\MailStudio` altında saklanır. Microsoft önbelleği MSAL korumalı depolamasını kullanır; Google yolu mevcut kodda `FileDataStore` dosyalarıdır. Hesap şifrelerinin DPAPI koruması tüm jeton dosyalarının ayrıca şifrelendiği anlamına gelmez. Bu klasörü diğer bilgisayara giriş aktarmak için kopyalamayın; orada yeniden oturum açın.
