namespace MailStudio.Views;

/// <summary>Page of the rich (HTML) editor in the new message window. Runs as contenteditable inside WebView2.</summary>
public static class EditorHtml
{
    public static string Page => Template.Replace("__NONCE__", Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)))
        .Replace("__SCHEME__", MailStudio.Services.ThemeManager.IsDark ? "dark" : "light")
        .Replace("__BG__", MailStudio.Services.ThemeManager.IsDark ? "#1E293B" : "#fff")
        .Replace("__FG__", MailStudio.Services.ThemeManager.IsDark ? "#E2E8F0" : "#222")
        .Replace("__RULE__", MailStudio.Services.ThemeManager.IsDark ? "#475569" : "#b5c4df");
    const string Template = """
<!doctype html>
<html lang="tr"><head><meta charset="utf-8">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; script-src 'nonce-__NONCE__'; style-src 'unsafe-inline'; img-src data: https: http:; connect-src 'none'; frame-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none'">
<style>
  :root { color-scheme: __SCHEME__; }
  html, body { margin:0; padding:0; background:__BG__; height:100%; }
  body { font-family: Calibri, 'Segoe UI', sans-serif; font-size: 11pt; color:__FG__; }
  #ms-root { padding: 12px 16px 40px 16px; outline: none; min-height: 100%; box-sizing: border-box; word-wrap: break-word; }
  #ms-draft { min-height: 120px; outline: none; }
  #ms-quote { margin-top: 18px; }
  #ms-quote .ms-hdr { border-top: 1px solid __RULE__; padding-top: 6px; margin-bottom: 10px; font-size: 11pt; }
  img { max-width: 100%; }
  table.ms-table { border-collapse: collapse; }
  table.ms-table td { border: 1px solid #999; padding: 4px 8px; min-width: 40px; }
  blockquote { margin: 0 0 0 8px; padding-left: 10px; border-left: 2px solid #ccc; }
</style></head>
<body>
<div id="ms-root" contenteditable="true" spellcheck="true"><div id="ms-draft" class="ms-draft-section"><div><br></div></div><div id="ms-signature" class="ms-signature-section"></div><div id="ms-quote" class="ms-quote-section"></div></div>
<script nonce="__NONCE__">
  const root = document.getElementById('ms-root');
  const draft = () => document.getElementById('ms-draft');
  const quote = () => document.getElementById('ms-quote');
  document.execCommand('styleWithCSS', false, true);
  document.execCommand('defaultParagraphSeparator', false, 'div');
  let lastRange = null;
  let rewriteRange = null;
  document.addEventListener('selectionchange', () => {
    const s = window.getSelection();
    if (s.rangeCount && root.contains(s.anchorNode)) lastRange = s.getRangeAt(0).cloneRange();
    report();
  });
  function restore() {
    root.focus();
    if (lastRange) { const s = window.getSelection(); s.removeAllRanges(); s.addRange(lastRange); }
  }
  function report() {
    try {
      chrome.webview.postMessage(JSON.stringify({ t: 'state',
        b: document.queryCommandState('bold'), i: document.queryCommandState('italic'),
        u: document.queryCommandState('underline') }));
    } catch (e) {}
  }
  function dirty() { try { chrome.webview.postMessage(JSON.stringify({ t: 'dirty' })); } catch (e) {} }
  root.addEventListener('input', dirty);
  root.addEventListener('paste', e => {
    e.preventDefault();
    const data = e.clipboardData;
    const h = data?.getData('text/html');
    if (h) chrome.webview.postMessage(JSON.stringify({ t: 'paste', html: h }));
    else { restore(); document.execCommand('insertText', false, data?.getData('text/plain') || ''); dirty(); }
  });
  // WebView2 has its own native drop surface; WPF's parent Drop event cannot receive these files.
  document.addEventListener('dragover', e => {
    if ([...(e.dataTransfer?.types || [])].includes('Files')) {
      e.preventDefault(); e.dataTransfer.dropEffect = 'copy';
    }
  });
  document.addEventListener('drop', e => {
    const files = [...(e.dataTransfer?.files || [])];
    if (files.length) {
      e.preventDefault(); e.stopPropagation();
      if (typeof chrome.webview.postMessageWithAdditionalObjects === 'function')
        chrome.webview.postMessageWithAdditionalObjects(JSON.stringify({ t: 'files' }), files);
      else chrome.webview.postMessage(JSON.stringify({ t: 'files-unavailable' }));
    } else {
      const h = e.dataTransfer?.getData('text/html');
      if (h) { e.preventDefault(); chrome.webview.postMessage(JSON.stringify({ t: 'paste', html: h })); }
    }
  });

  // --- @ mentions ---
  let mentionBox = null;
  let mentionStartNode = null;
  let mentionStartOffset = -1;
  let mentionQuery = '';
  let mentionMatches = [];
  let mentionSelectedIndex = 0;

  function closeMentionBox() {
    if (mentionBox) { mentionBox.remove(); mentionBox = null; }
    mentionMatches = [];
    mentionQuery = '';
    mentionStartNode = null;
    mentionStartOffset = -1;
  }

  function checkMentionInput() {
    const s = window.getSelection();
    if (!s.rangeCount || !s.isCollapsed) { closeMentionBox(); return; }
    const r = s.getRangeAt(0);
    const node = r.startContainer;
    if (node && node.nodeType === Node.TEXT_NODE) {
      const text = node.textContent.substring(0, r.startOffset);
      const match = /(?:^|\s)@([a-zA-Z0-9_\u00C0-\u017F]*)$/.exec(text);
      if (match) {
        mentionQuery = match[1];
        mentionStartNode = node;
        mentionStartOffset = match.index + (match[0].startsWith(' ') ? 1 : 0);
        try {
          chrome.webview.postMessage(JSON.stringify({ t: 'mention-query', query: mentionQuery }));
        } catch(e) {}
        return;
      }
    }
    closeMentionBox();
  }

  function renderMentionItems() {
    if (!mentionBox) return;
    mentionBox.innerHTML = '';
    mentionMatches.forEach((item, index) => {
      const row = document.createElement('div');
      row.style.cssText = 'padding: 6px 10px; cursor: pointer; display: flex; align-items: center; border-bottom: 1px solid rgba(128,128,128,0.15);' +
        (index === mentionSelectedIndex ? 'background: #0078D4; color: #fff;' : 'background: transparent; color: inherit;');

      const avatar = document.createElement('span');
      avatar.style.cssText = 'display: inline-flex; align-items: center; justify-content: center; width: 22px; height: 22px; border-radius: 11px; background: ' +
        (index === mentionSelectedIndex ? '#fff' : '#0078D4') + '; color: ' + (index === mentionSelectedIndex ? '#0078D4' : '#fff') + '; font-size: 9pt; font-weight: bold; margin-right: 8px; flex-shrink: 0;';
      const nameParts = (item.name || item.address || '?').split(' ');
      avatar.textContent = (nameParts[0][0] + (nameParts.length > 1 ? nameParts[1][0] : '')).toUpperCase();
      row.appendChild(avatar);

      const info = document.createElement('div');
      info.style.cssText = 'overflow: hidden; text-overflow: ellipsis; white-space: nowrap;';
      const nameSpan = document.createElement('div');
      nameSpan.style.cssText = 'font-weight: 600; font-size: 10pt; line-height: 1.2;';
      nameSpan.textContent = item.name || item.address;
      info.appendChild(nameSpan);

      if (item.name && item.address) {
        const addrSpan = document.createElement('div');
        addrSpan.style.cssText = 'font-size: 8.5pt; opacity: 0.8; line-height: 1.1;';
        addrSpan.textContent = item.address;
        info.appendChild(addrSpan);
      }
      row.appendChild(info);

      row.addEventListener('mousedown', e => {
        e.preventDefault();
        selectMention(index);
      });
      mentionBox.appendChild(row);
    });
  }

  function selectMention(index) {
    const item = mentionMatches[index];
    if (!item) { closeMentionBox(); return; }

    const s = window.getSelection();
    const r = document.createRange();
    if (mentionStartNode && mentionStartNode.parentNode) {
      const currentOffset = Math.min(mentionStartNode.textContent.length, mentionStartOffset + mentionQuery.length + 1);
      r.setStart(mentionStartNode, mentionStartOffset);
      r.setEnd(mentionStartNode, currentOffset);
      r.deleteContents();

      const span = document.createElement('span');
      span.className = 'ms-mention';
      span.style.color = '#0078D4';
      span.style.fontWeight = '600';
      span.textContent = '@' + item.name;
      r.insertNode(span);

      const space = document.createTextNode('\u00A0');
      span.parentNode.insertBefore(space, span.nextSibling);
      r.setStartAfter(space);
      r.collapse(true);
      s.removeAllRanges();
      s.addRange(r);
      lastRange = r.cloneRange();
    }

    closeMentionBox();
    dirty();
    try {
      chrome.webview.postMessage(JSON.stringify({ t: 'mention-selected', name: item.name, address: item.address }));
    } catch(e) {}
  }

  root.addEventListener('keyup', e => {
    if (mentionBox && (e.key === 'ArrowDown' || e.key === 'ArrowUp' || e.key === 'Enter' || e.key === 'Tab' || e.key === 'Escape')) return;
    checkMentionInput();
  });

  root.addEventListener('keydown', e => {
    if (mentionBox && mentionMatches.length > 0) {
      if (e.key === 'ArrowDown') {
        e.preventDefault();
        mentionSelectedIndex = (mentionSelectedIndex + 1) % mentionMatches.length;
        renderMentionItems();
        return;
      }
      if (e.key === 'ArrowUp') {
        e.preventDefault();
        mentionSelectedIndex = (mentionSelectedIndex - 1 + mentionMatches.length) % mentionMatches.length;
        renderMentionItems();
        return;
      }
      if (e.key === 'Enter' || e.key === 'Tab') {
        e.preventDefault();
        selectMention(mentionSelectedIndex);
        return;
      }
      if (e.key === 'Escape') {
        e.preventDefault();
        closeMentionBox();
        return;
      }
    }
  });

  window.ms = {
    showMentions(items) {
      mentionMatches = items || [];
      mentionSelectedIndex = 0;
      if (!mentionMatches.length) { closeMentionBox(); return; }
      if (!mentionBox) {
        mentionBox = document.createElement('div');
        mentionBox.id = 'ms-mention-box';
        mentionBox.style.cssText = 'position:fixed; background:' + (document.documentElement.style.colorScheme==='dark'?'#1E293B':'#ffffff') + '; border:1px solid #0078D4; border-radius:6px; box-shadow:0 4px 14px rgba(0,0,0,0.25); z-index:9999; min-width:240px; max-width:340px; overflow:hidden;';
        document.body.appendChild(mentionBox);

        const s = window.getSelection();
        if (s.rangeCount) {
          const rect = s.getRangeAt(0).getBoundingClientRect();
          mentionBox.style.left = Math.min(window.innerWidth - 260, Math.max(10, rect.left)) + 'px';
          mentionBox.style.top = Math.min(window.innerHeight - 150, rect.bottom + 4) + 'px';
        }
      }
      renderMentionItems();
    },
    closeMentions() { closeMentionBox(); },
    exec(cmd, val) { restore(); document.execCommand(cmd, false, val ?? null); dirty(); report(); },
    fontSize(pt) {
      restore();
      document.execCommand('fontSize', false, '7');
      root.querySelectorAll('font[size="7"]').forEach(f => { f.removeAttribute('size'); f.style.fontSize = pt + 'pt'; });
      root.querySelectorAll('span').forEach(s => { if (s.style.fontSize === 'xxx-large') s.style.fontSize = pt + 'pt'; });
    },
    insertHtml(h) { restore(); document.execCommand('insertHTML', false, h); dirty(); },
    link(url) { restore(); if (/^(https?:|mailto:)/i.test(url || '')) { document.execCommand('createLink', false, url); dirty(); } },
    table(r, c) {
      let h = '<table class="ms-table">';
      for (let i = 0; i < r; i++) { h += '<tr>'; for (let j = 0; j < c; j++) h += '<td><br></td>'; h += '</tr>'; }
      this.insertHtml(h + '</table><div><br></div>');
    },
    html() { return root.innerHTML; },
    draftHtml() { return draft().innerHTML; },
    draftText() { return draft().innerText; },
    quoteHtml() { return quote().innerHTML; },
    setDraft(h) { draft().innerHTML = h || '<div><br></div>'; },
    setQuote(h) { quote().innerHTML = h || ''; },
    setSignature(h) { document.getElementById('ms-signature').innerHTML = h || ''; dirty(); },
    reloadImages() { root.querySelectorAll('img').forEach(image=>{const src=image.getAttribute('src');if(src&&/^https?:/i.test(src)){image.removeAttribute('src');image.setAttribute('src',src);}}); },
    rewriteSnapshot() {
      const s=window.getSelection(); rewriteRange=null;
      if(s.rangeCount && !s.isCollapsed && draft().contains(s.anchorNode) && draft().contains(s.focusNode)) rewriteRange=s.getRangeAt(0).cloneRange();
      return JSON.stringify({text:rewriteRange?rewriteRange.toString():draft().innerText,baseline:draft().innerHTML,selected:!!rewriteRange});
    },
    applyRewrite(text, baseline) {
      if(draft().innerHTML!==baseline) return false;
      if(rewriteRange && draft().contains(rewriteRange.commonAncestorContainer)) {
        rewriteRange.deleteContents();const fragment=document.createDocumentFragment();text.split('\n').forEach((line,i)=>{if(i)fragment.appendChild(document.createElement('br'));fragment.appendChild(document.createTextNode(line));});rewriteRange.insertNode(fragment);rewriteRange=null;
      } else { draft().innerHTML=''; text.split('\n').forEach(line=>{const d=document.createElement('div');d.textContent=line;if(!line)d.appendChild(document.createElement('br'));draft().appendChild(d);}); }
      dirty();return true;
    },
    appendToDraft(h) { draft().insertAdjacentHTML('beforeend', h); },
    focusStart() {
      root.focus();
      const r = document.createRange(); r.setStart(draft(), 0); r.collapse(true);
      const s = window.getSelection(); s.removeAllRanges(); s.addRange(r); lastRange = r.cloneRange();
    },
    // For sending: turn editor classes into inline styles (mail clients may ignore <style>)
    exportHtml() {
      const clone = root.cloneNode(true);
      clone.removeAttribute('contenteditable'); clone.removeAttribute('id'); clone.removeAttribute('spellcheck');
      clone.querySelectorAll('table.ms-table').forEach(t => { t.style.borderCollapse = 'collapse'; });
      clone.querySelectorAll('table.ms-table td').forEach(td => { td.style.border = '1px solid #999'; td.style.padding = '4px 8px'; });
      clone.querySelectorAll('#ms-quote .ms-hdr').forEach(h => { h.style.borderTop = '1px solid #b5c4df'; h.style.paddingTop = '6px'; });
      return '<div style="font-family:Calibri,\'Segoe UI\',sans-serif;font-size:11pt;color:#222">' + clone.innerHTML + '</div>';
    },
    snapshot() { return JSON.stringify({ html: this.exportHtml(), text: root.innerText, draftText: draft() ? draft().innerText : root.innerText, editable:this.draftHtml(), signature:document.getElementById('ms-signature').innerHTML, quote:this.quoteHtml() }); },
    text() { return root.innerText; }
  };
</script>
</body></html>
""";
}
