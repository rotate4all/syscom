// Edit mode: drag cards (and whole sections) into a new order, and resize cards by their corner handle.
// Pointer events, so it works the same with a mouse, a pen or a finger. With a mouse a card can be grabbed anywhere;
// on touch screens the grip handle starts a drag (so the page still scrolls when you swipe over a card).

const MOVE_THRESHOLD = 5;       // px before a mouse press on a card becomes a drag
const SETTLE_MS = 140;          // ignore new drop targets briefly after the layout moved, so it can't flip back and forth
const EDGE = 48;                // px from the top/bottom edge where the page scrolls by itself

/**
 * @param {HTMLElement} board    the page's board (groups → grids → cards)
 * @param {HTMLElement} scroller the element that scrolls vertically
 * @param {{ onReorder: () => void, onResize: (id: string, w: number, h: number) => void, maxRows: number }} opts
 */
export function enableDnd(board, scroller, { onReorder, onResize, maxRows }) {
  board.addEventListener('pointerdown', (e) => {
    if (!document.body.classList.contains('editing') || e.button > 0) return;
    const resize = e.target.closest('.resize');
    if (resize) { e.preventDefault(); startResize(resize.closest('.card'), e); return; }
    const grip = e.target.closest('.grip');
    if (grip) {
      e.preventDefault();
      const sec = grip.closest('.sec-head');
      if (sec) startDrag(sec.closest('.group'), e, true);
      else startDrag(grip.closest('.card'), e, false);
      return;
    }
    // Mouse: anywhere on a card except its buttons.
    const card = e.target.closest('.card');
    if (!card || e.pointerType !== 'mouse' || e.target.closest('button, input, select, a, .resize')) return;
    const x0 = e.clientX, y0 = e.clientY;
    const move = (m) => {
      if (Math.hypot(m.clientX - x0, m.clientY - y0) < MOVE_THRESHOLD) return;
      cleanup();
      startDrag(card, e, false, m);
    };
    const cleanup = () => { removeEventListener('pointermove', move); removeEventListener('pointerup', cleanup); };
    addEventListener('pointermove', move);
    addEventListener('pointerup', cleanup);
  });

  // ---- dragging
  function startDrag(item, down, isGroup, firstMove) {
    if (!item) return;
    if (isGroup) board.classList.add('sorting-sections');   // sections fold to their headers while being sorted
    const source = isGroup ? item.querySelector('.sec-head') : item;
    const rect = source.getBoundingClientRect();
    const ghost = source.cloneNode(true);
    ghost.classList.add('drag-ghost');
    Object.assign(ghost.style, { left: `${rect.left}px`, top: `${rect.top}px`, width: `${rect.width}px`, height: `${rect.height}px` });
    // Canvases don't clone their drawing; copy it so the ghost of a graph isn't blank.
    ghost.querySelectorAll('canvas').forEach((c, i) => {
      const from = source.querySelectorAll('canvas')[i];
      c.width = from.width; c.height = from.height;
      c.getContext('2d')?.drawImage(from, 0, 0);
    });
    document.body.append(ghost);
    item.classList.add('drag-source');
    document.body.classList.add('dragging');

    const before = order();
    const grab = { x: down.clientX - rect.left, y: down.clientY - rect.top };
    let pointer = { x: down.clientX, y: down.clientY };
    let settledAt = 0;
    let raf = 0;

    const onMove = (e) => {
      pointer = { x: e.clientX, y: e.clientY };
      ghost.style.left = `${pointer.x - grab.x}px`;
      ghost.style.top = `${pointer.y - grab.y}px`;
      target();
    };
    const tick = () => {
      // Scroll when held near the top or bottom edge.
      const r = scroller.getBoundingClientRect();
      const speed = pointer.y < r.top + EDGE ? -(r.top + EDGE - pointer.y) / 3
        : pointer.y > r.bottom - EDGE ? (pointer.y - (r.bottom - EDGE)) / 3 : 0;
      if (speed) { scroller.scrollTop += speed; target(); }
      raf = requestAnimationFrame(tick);
    };
    raf = requestAnimationFrame(tick);

    function target() {
      if (performance.now() - settledAt < SETTLE_MS) return;
      const under = document.elementFromPoint(pointer.x, pointer.y);
      if (!under || !board.contains(under)) return;
      const moved = isGroup ? placeGroup(under) : placeCard(under);
      if (moved) settledAt = performance.now();
    }

    function placeCard(under) {
      const over = under.closest('.card');
      if (over === item) return false;
      if (over) {
        const r = over.getBoundingClientRect();
        const after = pointer.x > r.left + r.width / 2;
        const ref = after ? over.nextElementSibling : over;
        if (ref === item || (after ? item.previousElementSibling === over : item.nextElementSibling === over)) return false;
        return flip(() => over.parentElement.insertBefore(item, ref));
      }
      // Empty space in a grid, the add tile or a section header: the end (or start) of that section.
      const grid = under.closest('.grid') || under.closest('.group')?.querySelector(':scope > .grid');
      if (!grid) return false;
      const head = under.closest('.sec-head');
      const tile = grid.querySelector(':scope > .add-tile');
      if (head) {
        const first = grid.querySelector(':scope > .card');
        if (first === item) return false;
        return flip(() => grid.insertBefore(item, first || tile));
      }
      const cards = [...grid.querySelectorAll(':scope > .card')].filter((c) => c !== item);
      const lastCard = cards[cards.length - 1];
      if (lastCard) {
        const r = lastCard.getBoundingClientRect();
        if (pointer.y < r.top || (pointer.y < r.bottom && pointer.x < r.right)) return false;
      }
      if (item.parentElement === grid && item.nextElementSibling === tile) return false;
      return flip(() => grid.insertBefore(item, tile));
    }

    function placeGroup(under) {
      const over = under.closest('.group');
      if (!over || over === item || over.classList.contains('untitled')) return false;
      const r = over.getBoundingClientRect();
      const after = pointer.y > r.top + r.height / 2;
      const ref = after ? over.nextElementSibling : over;
      if (ref === item || (after ? item.previousElementSibling === over : item.nextElementSibling === over)) return false;
      return flip(() => board.insertBefore(item, ref));
    }

    const onUp = () => {
      removeEventListener('pointermove', onMove);
      removeEventListener('pointerup', onUp);
      removeEventListener('pointercancel', onUp);
      cancelAnimationFrame(raf);
      board.classList.remove('sorting-sections');
      // Settle the ghost onto the card's new place, then drop it.
      const to = source.getBoundingClientRect();
      const anim = ghost.animate([
        { left: ghost.style.left, top: ghost.style.top },
        { left: `${to.left}px`, top: `${to.top}px` },
      ], { duration: 140, easing: 'ease-out' });
      anim.onfinish = anim.oncancel = () => {
        ghost.remove();
        item.classList.remove('drag-source');
        document.body.classList.remove('dragging');
      };
      if (order() !== before) onReorder();
    };
    addEventListener('pointermove', onMove);
    addEventListener('pointerup', onUp);
    addEventListener('pointercancel', onUp);
    if (firstMove) onMove(firstMove);
  }

  function order() {
    return [...board.querySelectorAll('.group, .card')].map((e) => e.dataset.id || e.dataset.section || '').join(',');
  }

  /** Moves elements and animates everything that shifted from its old place to its new one. */
  function flip(mutate) {
    const els = [...board.querySelectorAll('.card, .group')];
    const first = new Map(els.map((e) => [e, e.getBoundingClientRect()]));
    mutate();
    for (const e of els) {
      if (e.classList.contains('drag-source')) continue;
      const a = first.get(e);
      const b = e.getBoundingClientRect();
      const dx = a.left - b.left, dy = a.top - b.top;
      if (Math.abs(dx) > 1 || Math.abs(dy) > 1) {
        e.animate([{ transform: `translate(${dx}px, ${dy}px)` }, { transform: 'none' }], { duration: 160, easing: 'ease-out' });
      }
    }
    return true;
  }

  // ---- resizing: snaps to whole columns and rows
  function startResize(card, down) {
    const grid = card.parentElement;
    const cs = getComputedStyle(grid);
    const cols = Number(getComputedStyle(board).getPropertyValue('--cols')) || 4;
    const gap = parseFloat(cs.columnGap) || 0;
    const rowH = parseFloat(cs.gridAutoRows) || 56;
    const colW = (grid.clientWidth - gap * (cols - 1)) / cols;
    const rect = card.getBoundingClientRect();
    const start = { w: Number(card.dataset.w) || 1, h: Number(card.dataset.h) || 1 };
    let size = { ...start };
    const badge = document.createElement('span');
    badge.className = 'size-badge';
    card.append(badge);
    card.classList.add('resizing');
    document.body.classList.add('dragging');

    const onMove = (e) => {
      const w = Math.max(1, Math.min(cols, Math.round((e.clientX - rect.left + gap) / (colW + gap))));
      const h = Math.max(1, Math.min(maxRows, Math.round((e.clientY - rect.top + gap) / (rowH + gap))));
      if (w === size.w && h === size.h) return;
      size = { w, h };
      flip(() => {
        card.style.gridColumnEnd = `span ${w}`;
        card.style.gridRowEnd = `span ${h}`;
      });
      badge.textContent = `${w} × ${h}`;
    };
    const onUp = () => {
      removeEventListener('pointermove', onMove);
      removeEventListener('pointerup', onUp);
      removeEventListener('pointercancel', onUp);
      badge.remove();
      card.classList.remove('resizing');
      document.body.classList.remove('dragging');
      if (size.w !== start.w || size.h !== start.h) onResize(card.dataset.id, size.w, size.h);
    };
    badge.textContent = `${start.w} × ${start.h}`;
    addEventListener('pointermove', onMove);
    addEventListener('pointerup', onUp);
    addEventListener('pointercancel', onUp);
  }
}
