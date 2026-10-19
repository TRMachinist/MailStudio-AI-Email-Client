import json
import sys

def update_en(new_keys):
    path = 'src/MailStudio.Core/i18n/en.json'
    with open(path, 'r', encoding='utf-8') as f:
        d = json.load(f)

    for k, v in new_keys.items():
        if len(k.strip()) <= 2:
            raise ValueError(f'Key too short (<=2 chars): {k}')
        d[k] = v

    with open(path, 'w', encoding='utf-8') as f:
        json.dump(d, f, ensure_ascii=False, indent=1, sort_keys=True)
        f.write('\n')

    print(f'Updated {path}, total keys: {len(d)}')

if __name__ == '__main__':
    keys = {
        'Gönderen şunu içeriyor': 'From contains',
        'Kime / Bilgi şunu içeriyor': 'To / Cc contains',
        'Konu şunu içeriyor': 'Subject contains',
        'Gövde şunu içeriyor': 'Body contains',
        'Eki var': 'Has attachment',
        'Ek türü (uzantı)': 'Attachment type (extension)',
        'Hesap şunu içeriyor': 'Account contains',
        'Klasör şunu içeriyor': 'Folder contains',
        'Önem derecesi': 'Importance',
        'Büyüklük şundan fazla (KB)': 'Size larger than (KB)',
        'Büyüklük şundan az (KB)': 'Size smaller than (KB)',
        'Klasöre taşı': 'Move to folder',
        "Silinmiş Öğeler'e taşı": 'Move to Deleted Items',
        'Okundu olarak işaretle': 'Mark as read',
        'Bayrakla': 'Flag',
        'Kategori ata': 'Assign category',
        'Görev oluştur': 'Create task',
        'Skill çalıştır': 'Run skill',
        'Sonraki kuralları işleme': 'Stop processing more rules',
        'Klasöre taşındı: {0}': 'Moved to folder: {0}',
        "Silinmiş Öğeler'e taşındı": 'Moved to Deleted Items',
        'Okundu işaretlendi': 'Marked as read',
        'Bayraklandı': 'Flagged',
        'Kategori atandı: {0}': 'Category assigned: {0}',
        'Görev oluşturuldu: {0}': 'Task created: {0}',
        'Skill çalıştırıldı: {0}': 'Skill executed: {0}',
        'Yapay zeka geçerli bir kural döndürmedi.': 'AI did not return a valid rule.',
        'Kural okunamadı.': 'Could not read rule.',
        'Kuralda tanınan bir koşul yok.': 'No recognized condition in rule.',
        'Kuralda tanınan bir eylem yok.': 'No recognized action in rule.',
        'Kurallar ve Uyarılar': 'Rules and Alerts',
        'E-posta Kuralları': 'Email Rules',
        'Gelen iletiler için otomatik kurallar. Kurallar yukarıdan aşağı denenir; eşleşen kuraldaki eylemler uygulanır.': 'Automated rules for incoming messages. Rules are evaluated top to bottom; matching actions are executed.',
        'Kurallar': 'Rules',
        'Doğal dille kural oluştur': 'Create rule with natural language',
        'Örn: "Anna\'dan gelen teklifleri Teklifler klasörüne taşı ve bayrakla"': 'E.g.: "Move quotation requests from Anna to Quotations and flag them"',
        'Kural adı': 'Rule name',
        'Tüm koşullar sağlandığında (VE)': 'When all conditions match (AND)',
        'Herhangi bir koşul sağlandığında (VEYA)': 'When any condition matches (OR)',
        'Koşullar': 'Conditions',
        '+ Koşul ekle': '+ Add condition',
        'Eylemler': 'Actions',
        '+ Eylem ekle': '+ Add action',
        'Bu kuralı test et': 'Test this rule',
        'Klasörde şimdi çalıştır': 'Run on folder now',
        'Kaydet ve Kapat': 'Save and Close',
        'Kapat': 'Close',
        '(Koşul yok)': '(No conditions)',
        '(Eylem yok)': '(No actions)',
        'Sınanacak örnek ileti yok.': 'No sample messages to test.',
        'Örnek {0} iletiden {1} tanesi bu kurala uyuyor.': '{1} of {0} sample messages match this rule.',
        '{0} ile kural taslağı hazırlanıyor...': 'Drafting rule with {0}...',
        "Kural oluşturuldu. İnceleyip 'Kaydet ve Kapat' ile onaylayın.": "Rule created. Review and confirm with 'Save and Close'.",
        'Kural oluşturulamadı: ': 'Could not create rule: ',
        'Kurallar ve Biçim': 'Rules & Formatting',
        'Gelen iletiler için otomatik kurallar (taşı, bayrakla, görev yap). Yapay zekayla da oluşturulabilir.': 'Automated rules for incoming messages (move, flag, create task). Can also be created with AI.',
        'Bunun gibi mailler için kural oluştur…': 'Create rule for messages like this…',
        'Kurallar: {0} kural kayıtlı.': 'Rules: {0} rules saved.',
        '{0} iletileri için kural': 'Rule for {0} messages',
        'Kural çalıştırıldı: {0} ileti etkilendi.': 'Rule executed: {0} messages affected.',
        'Cevap gelmezse hatırlat': 'Remind if no reply',
        'Cevap gelmezse hatırlat:': 'Remind if no reply:',
        'Cevap bekleniyor': 'Awaiting reply',
        'Cevap bekleniyor: {0}': 'Awaiting reply: {0}',
        '2 gün': '2 days',
        '3 gün': '3 days',
        '5 gün': '5 days',
        '1 hafta': '1 week',
        '2 hafta': '2 weeks',
        'Hatırlatma maili yaz': 'Compose reminder',
        "Hatırlatma maili hazır; kontrol edip Gönder'e basın.": 'Reminder email ready; review and click Send.',
        'Merhaba,\n\nAşağıdaki konuda geri dönüşünüzü rica ederim.\n\nİyi çalışmalar.': 'Hello,\n\nI kindly ask for your feedback on the matter below.\n\nBest regards.',
        'Alıcılar: {0}\nGönderim tarihi: {1:dd.MM.yyyy HH:mm}\nSon tarih: {2:dd.MM.yyyy}': 'Recipients: {0}\nSent date: {1:dd.MM.yyyy HH:mm}\nDue date: {2:dd.MM.yyyy}',
        'Önceki İleti': 'Previous Message',
    }
    update_en(keys)
