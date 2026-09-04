(() => {
  const form = document.querySelector('[data-rack-editor]');
  if (!form) return;

  const pinInput = form.querySelector('[data-rack-pin]');
  const saveButton = form.querySelector('[data-rack-save]');
  if (!pinInput || !saveButton) return;

  pinInput.addEventListener('keydown', (event) => {
    if (event.key !== 'Enter' || event.isComposing) return;

    event.preventDefault();
    form.requestSubmit(saveButton);
  });
})();
