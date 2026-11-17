'use strict';
const $ = (id) => document.getElementById(id);
const JOB_STATE = {
  accepted_by_agent: ['در انتظار ارسال به چاپگر', 'st-wait'],
  submitting: ['در حال ارسال…', 'st-wait'],
  submitted_to_spooler: ['به صف ویندوز رفت (چاپ‌شدن تأیید نشده)', 'st-wait'],
  confirmed_completed: ['چاپ‌شده (تأیید ویندوز)', 'st-ok'],
  failed: ['ناموفق — چیزی چاپ نشد', 'st-bad'],
  cancelled: ['لغو شد', 'st-bad'],
  unknown: ['نتیجه نامعلوم — خودکار دوباره چاپ نمی‌شود', 'st-bad'],
};
const WORKER = {
  Running: ['در حال کار', 'ok'], Starting: ['در حال اتصال…', 'warn'], Stopped: ['متوقف', 'warn'],
  NotPaired: ['نیاز به اتصال', 'warn'], NoPrinter: ['نیاز به انتخاب چاپگر', 'warn'], PrinterMissing: ['چاپگر پیدا نشد', 'bad'],
  CredentialRejected: ['دسترسی رد شد', 'bad'], ContractUnsupported: ['نیاز به به‌روزرسانی', 'bad'], JournalLocked: ['نمونه‌ی دیگر در حال اجراست', 'bad'],
  JournalProblem: ['مشکل در پرونده‌ی کارها', 'bad'], StorageFailure: ['خطای دیسک', 'bad'], Failed: ['خطا', 'bad'],
};
let current = null;
let formsFilled = false;

async function api(path, body) {
  const opt = body === undefined ? {} : { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-LP': '1' }, body: JSON.stringify(body) };
  const r = await fetch(path, opt);
  let j = {};
  try { j = await r.json(); } catch { /* not JSON */ }
  if (r.status === 401) { showBanner('نشست پایان یافته است. پنجره را از آیکن برنامه دوباره باز کنید.', 'bad'); throw new Error('auth'); }
  return { ok: r.ok && j.ok !== false, message: j.message || j.error || '', data: j };
}
function toast(msg, ok = true) {
  const t = $('toast'); t.textContent = msg; t.hidden = false; t.style.background = ok ? '' : 'var(--red)'; t.style.color = ok ? '' : '#fff';
  clearTimeout(toast.t); toast.t = setTimeout(() => { t.hidden = true; }, 7000);
}
function showBanner(msg, kind) { const b = $('banner'); b.textContent = msg; b.className = 'banner ' + (kind || ''); b.hidden = !msg; }
const fmt = (iso) => { if (!iso) return ''; const d = new Date(iso); return isNaN(d) ? '' : d.toLocaleString('fa-IR', { dateStyle: 'short', timeStyle: 'short' }); };

function render(s) {
  current = s;
  $('version').textContent = 'نسخه ' + s.version + ' (آزمایشی، امضانشده)';
  const [label, kind] = WORKER[s.state] || [s.state, 'warn'];
  const pill = $('pill');
  pill.textContent = s.paused && s.state === 'Running' ? 'متوقف‌شده توسط شما' : label;
  pill.className = 'pill ' + (s.paused && s.state === 'Running' ? 'warn' : kind);

  let banner = '', bk = kind;
  if (s.state !== 'Running') banner = s.message;
  else if (!s.erpReachable) { banner = 'اتصال به سرور برقرار نیست؛ کارهای جدید نمی‌رسند. برنامه خودش دوباره تلاش می‌کند.'; bk = 'warn'; }
  showBanner(banner, bk);

  $('connect-done').hidden = !s.paired;
  $('connect-form').hidden = !!s.paired;
  if (s.paired) { $('origin').textContent = s.paired.origin; $('agent-name').textContent = s.paired.name; }

  const sel = $('in-queue');
  if (document.activeElement !== sel) {
    const old = sel.value || (s.printer && s.printer.queue) || '';
    sel.replaceChildren(...s.queues.map((q) => { const o = document.createElement('option'); o.value = q; o.textContent = q; return o; }));
    if (s.queues.includes(old)) sel.value = old;
  }
  if (s.printer && !formsFilled) {
    $('in-language').value = s.printer.language; $('in-dpi').value = String(s.printer.dpi);
    $('in-width').value = s.printer.widthMm; $('in-height').value = s.printer.heightMm; formsFilled = true;
  }
  $('card-printer').querySelector('h2').lastChild.textContent = ' چاپگر' + (s.printer ? ' — ' + s.printer.queue : ' (انتخاب نشده)');

  const pb = $('btn-pause');
  pb.textContent = s.paused ? 'ادامه ارسال' : 'توقف ارسال';
  pb.className = 'btn big' + (s.paused ? '' : ' secondary');
  $('pause-note').textContent = s.paused
    ? 'ارسال متوقف است: کار جدیدی گرفته یا چاپ نمی‌شود. کارهای گرفته‌شده می‌مانند و با «ادامه» ارسال می‌شوند.'
    : 'با «توقف» می‌توانید موقتاً جلوی گرفتن و چاپ کار جدید را بگیرید (مثلاً هنگام عوض کردن کاغذ).';
  if (document.activeElement !== $('in-autostart')) $('in-autostart').checked = !!s.autostart;
  $('quarantine').hidden = !s.quarantine;
  $('outbox').textContent = (s.outbox.accepts + s.outbox.events) ? `(${s.outbox.accepts + s.outbox.events} گزارش در انتظار ارسال به سرور)` : '';

  const body = $('jobs'); body.replaceChildren();
  $('no-jobs').hidden = s.jobs.length > 0;
  for (const j of s.jobs) {
    const tr = document.createElement('tr');
    const cell = (text, cls) => { const td = document.createElement('td'); td.textContent = text; if (cls) td.className = cls; tr.appendChild(td); return td; };
    cell(fmt(j.updatedAt || j.createdAt));
    cell(j.id.slice(0, 8) + (j.reprint ? ' (چاپ مجدد)' : '') + (j.copies > 1 ? ` ×${j.copies}` : ''), 'id');
    const [text, cls] = JOB_STATE[j.state] || [j.state, ''];
    cell(text, cls);
    const notes = [];
    if (j.error) notes.push('کد: ' + j.error);
    if (j.spoolerJobRef) notes.push('شماره در صف: ' + j.spoolerJobRef);
    if (j.blocked) notes.push(`گزارش به سرور گیر کرده (${j.blocked.code})`);
    cell(notes.join(' · '));
    const act = document.createElement('td');
    if (j.state === 'accepted_by_agent') act.appendChild(button('لغو', 'btn small danger', () => cancelJob(j)));
    if (j.state === 'submitted_to_spooler' && j.spoolerJobRef) act.appendChild(button('حذف از صف ویندوز', 'btn small secondary', () => deleteJob(j)));
    tr.appendChild(act); body.appendChild(tr);
  }
  $('log').textContent = s.log.join('\n');
}
function button(text, cls, fn) { const b = document.createElement('button'); b.className = cls; b.textContent = text; b.addEventListener('click', fn); return b; }

async function act(promise) {
  try { const r = await promise; toast(r.message || (r.ok ? 'انجام شد.' : 'انجام نشد.'), r.ok); await refresh(); return r; }
  catch (e) { if (e.message !== 'auth') toast('ارتباط با برنامه برقرار نشد.', false); }
}
function cancelJob(j) { if (confirm('این کار لغو شود؟ چیزی برای چاپ فرستاده نمی‌شود.')) act(api(`/api/jobs/${j.id}/cancel`, {})); }
function deleteJob(j) {
  if (confirm('کار از صف چاپ ویندوز حذف شود؟\nممکن است بخشی از آن چاپ شده باشد؛ وضعیت «نامعلوم» ثبت می‌شود و دوباره ارسال نمی‌شود.'))
    act(api(`/api/jobs/${j.id}/spooler-delete`, { confirm: true }));
}

$('connect-form').addEventListener('submit', async (e) => {
  e.preventDefault();
  const btn = e.submitter; btn.disabled = true;
  const r = await act(api('/api/pair', { origin: $('in-origin').value, code: $('in-code').value, name: $('in-name').value }));
  btn.disabled = false; if (r && r.ok) $('in-code').value = '';
});
$('btn-unpair').addEventListener('click', () => { if (confirm('اتصال این کامپیوتر به سرور قطع شود؟\nپرونده‌ی ثبت کارها نگه داشته می‌شود.')) act(api('/api/unpair', { confirm: true })); });
$('printer-form').addEventListener('submit', async (e) => {
  e.preventDefault();
  if (current && current.printer && current.printer.queue !== $('in-queue').value && !confirm('چاپگر عوض شود؟ کارهای در انتظار چاپگر قبلی ناموفق ثبت می‌شوند.')) return;
  await act(api('/api/printer', { queue: $('in-queue').value, language: $('in-language').value, dpi: Number($('in-dpi').value), widthMm: Number($('in-width').value), heightMm: Number($('in-height').value) }));
  formsFilled = false;
});
$('btn-pause').addEventListener('click', () => act(api('/api/pause', { paused: !(current && current.paused) })));
$('in-autostart').addEventListener('change', (e) => act(api('/api/autostart', { enabled: e.target.checked })));
$('btn-quarantine').addEventListener('click', () => {
  if (confirm('قرنطینه برداشته شود؟\nکارهای «در انتظار ارسال» بدون چاپ ناموفق ثبت می‌شوند، چون معلوم نیست پیش‌تر چاپ شده‌اند یا نه. چاپ دوباره فقط از سامانه ممکن است.')) act(api('/api/quarantine', { confirm: true }));
});
$('btn-quit').addEventListener('click', async () => {
  if (!confirm('برنامه کاملاً بسته شود؟ تا باز کردن دوباره، کاری دریافت یا چاپ نمی‌شود.')) return;
  try { await api('/api/quit', {}); } catch { /* the app is closing */ }
  document.body.textContent = 'برنامه بسته شد. می‌توانید این پنجره را ببندید.';
});

async function refresh() {
  try { const r = await api('/api/state'); if (r.data && r.data.state) render(r.data); }
  catch (e) { if (e.message !== 'auth') showBanner('ارتباط با برنامه قطع شد. اگر برنامه را بسته‌اید، از آیکن دوباره باز کنید.', 'bad'); }
}
refresh();
setInterval(refresh, 2000);
