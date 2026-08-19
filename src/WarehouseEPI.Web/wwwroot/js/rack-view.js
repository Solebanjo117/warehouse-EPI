(() => {
  const view = document.querySelector('[data-rack-view]');
  if (!view) return;

  const panel = view.querySelector('.rack-detail-panel');
  const placeholder = view.querySelector('.rack-detail-placeholder');
  const links = [...view.querySelectorAll('[data-rack-open]')];
  const details = [...view.querySelectorAll('[data-rack-detail]')];
  let selected = null;

  const close = () => {
    details.forEach(detail => { detail.hidden = true; });
    links.forEach(link => link.classList.remove('is-selected'));
    placeholder.hidden = false;
    selected = null;
  };

  const open = id => {
    const detail = view.querySelector(`[data-rack-detail="${id}"]`);
    if (!detail) return;
    details.forEach(item => { item.hidden = item !== detail; });
    links.forEach(link => link.classList.toggle('is-selected', link.dataset.rackOpen === id));
    placeholder.hidden = true;
    selected = id;
    panel?.scrollTo({ top: 0, behavior: 'smooth' });
  };

  links.forEach(link => link.addEventListener('click', event => {
    event.preventDefault();
    open(link.dataset.rackOpen);
  }));
  view.querySelectorAll('[data-rack-close]').forEach(button => button.addEventListener('click', close));
  document.addEventListener('keydown', event => {
    if (event.key === 'Escape' && selected) close();
  });
})();
