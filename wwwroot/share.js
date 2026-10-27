'use strict';
document.querySelectorAll('[data-copy]').forEach(button => {
  button.addEventListener('click', async () => {
    const input = document.getElementById(button.dataset.copy);
    const status = document.getElementById('copy-status');
    try {
      await navigator.clipboard.writeText(input.value);
      status.textContent = 'Link copied to clipboard.';
    } catch {
      input.focus(); input.select();
      status.textContent = 'Link selected. Press Ctrl+C (or ⌘C) to copy.';
    }
  });
});
