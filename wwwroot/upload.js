'use strict';
const $ = id => document.getElementById(id);
let config, selected, busy = false;
const size = bytes => bytes >= 1048576 ? `${(bytes / 1048576).toFixed(2)} MB` : bytes >= 1024 ? `${(bytes / 1024).toFixed(1)} KB` : `${bytes} bytes`;
function status(message, error = false) {
  $('status').textContent = message;
  $('status').dataset.error = String(error);
}
function select(file) {
  if (busy || !config || !file) return;
  if (!file.size || file.size > config.maxFileBytes) {
    status(!file.size ? 'Empty files cannot be uploaded.' : `Choose a file smaller than ${size(config.maxFileBytes)}.`, true);
    return;
  }
  selected = file;
  $('file-name').textContent = file.name;
  $('file-size').textContent = size(file.size);
  $('selection').hidden = false;
  $('result').hidden = true;
  $('key-field').hidden = !config.requiresKey;
  status('Ready when you are.');
}
function reset() {
  selected = undefined;
  $('file-input').value = '';
  $('selection').hidden = true;
  $('result').hidden = true;
  $('upload-progress').hidden = true;
  $('upload-key').value = '';
  status('');
}
$('browse').addEventListener('click', () => $('file-input').click());
$('file-input').addEventListener('change', e => select(e.target.files[0]));
$('clear').addEventListener('click', reset);
$('another').addEventListener('click', reset);
// Prevent the browser from navigating to a dropped file.
window.addEventListener('dragover', e => e.preventDefault());
window.addEventListener('drop', e => e.preventDefault());
const zone = $('drop-zone');
let depth = 0;
zone.addEventListener('dragenter', e => { e.preventDefault(); depth++; if (!busy) zone.classList.add('dragging'); });
zone.addEventListener('dragleave', () => { if (--depth <= 0) { depth = 0; zone.classList.remove('dragging'); } });
zone.addEventListener('dragover', e => e.preventDefault());
zone.addEventListener('drop', e => {
  e.preventDefault(); depth = 0; zone.classList.remove('dragging');
  if (busy) return;
  if (e.dataTransfer.files.length !== 1) return status('Please drop one file at a time.', true);
  select(e.dataTransfer.files[0]);
});
$('upload').addEventListener('click', () => {
  if (!selected || busy) return;
  if (config.requiresKey && !$('upload-key').value) return status('Enter the upload key to continue.', true);
  busy = true;
  ['browse', 'clear', 'upload', 'upload-key'].forEach(id => $(id).disabled = true);
  $('upload-progress').hidden = false;
  $('progress-label').textContent = 'Uploading… 0%';
  $('progress-fill').style.width = '0%';
  const bar = document.querySelector('[role="progressbar"]');
  bar.setAttribute('aria-valuenow', '0');
  status('');
  const xhr = new XMLHttpRequest();
  xhr.open('POST', `/api/files?name=${encodeURIComponent(selected.name)}`);
  xhr.setRequestHeader('Content-Type', 'application/octet-stream');
  if (config.requiresKey) xhr.setRequestHeader('X-Upload-Key', $('upload-key').value);
  xhr.upload.onprogress = e => {
    if (!e.lengthComputable) return;
    const percent = Math.round(e.loaded / e.total * 100);
    $('progress-fill').style.width = `${percent}%`;
    bar.setAttribute('aria-valuenow', String(percent));
    $('progress-label').textContent = percent === 100 ? 'Upload received. Compressing and saving…' : `Uploading… ${percent}%`;
  };
  function finish() {
    busy = false;
    ['browse', 'clear', 'upload', 'upload-key'].forEach(id => $(id).disabled = false);
    $('upload-progress').hidden = true;
  }
  xhr.onload = () => {
    finish();
    let data;
    try { data = JSON.parse(xhr.responseText); } catch { status('The server could not complete the upload. Please try again.', true); return; }
    if (xhr.status !== 201) return status(data.error || (xhr.status === 429 ? 'Too many uploads. Wait a minute and try again.' : 'Upload failed. Please try again.'), true);
    $('selection').hidden = true;
    $('result').hidden = false;
    $('direct-link').value = data.downloadUrl;
    $('share-link').value = data.shareUrl;
    $('open-file').href = data.shareUrl;
    $('result-details').textContent = `${data.name} · ${size(data.size)} · ${data.compressed ? `${Math.round((1 - data.storedSize / data.size) * 100)}% less storage with lossless compression.` : 'Already compact. Original stored without extra overhead.'}`;
    $('copy-status').textContent = '';
    $('upload-key').value = '';
    status('Upload complete. Your links are ready below.');
    $('result').scrollIntoView({ behavior: 'smooth', block: 'nearest' });
  };
  xhr.onerror = () => { finish(); status('Connection lost. Check your connection and try again.', true); };
  xhr.send(selected);
});
window.addEventListener('beforeunload', e => { if (busy) { e.preventDefault(); e.returnValue = ''; } });
fetch('/api/config').then(response => { if (!response.ok) throw new Error(); return response.json(); }).then(data => {
  config = data;
  $('limits').textContent = `One file at a time · Up to ${size(data.maxFileBytes)} · Any file type`;
  $('browse').disabled = false;
}).catch(() => {
  $('limits').textContent = 'The server is unavailable. Reload this page to try again.';
});
