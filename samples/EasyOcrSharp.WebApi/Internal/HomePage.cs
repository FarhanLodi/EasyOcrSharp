namespace EasyOcrSharp.WebApi.Internal;

/// <summary>
/// The one-page browser UI served at <c>GET /</c>.
/// </summary>
/// <remarks>
/// Deliberately a single embedded string with no framework, no build step and no external asset: the
/// point of the sample is that <c>docker run</c> then "open the page and drop in a photo" is the whole
/// demo. There is nothing to copy into production here — the endpoints are the sample.
/// </remarks>
internal static class HomePage
{
    /// <summary>The complete HTML document, ready to be returned as <c>text/html</c>.</summary>
    public const string Html = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>EasyOcrSharp Web API sample</title>
<style>
  :root { color-scheme: light dark; --fg: #17181c; --bg: #fbfbfa; --line: #dcdcd6; --accent: #7a4d2b; }
  @media (prefers-color-scheme: dark) {
    :root { --fg: #e9e9e6; --bg: #1b1b19; --line: #3a3a36; --accent: #d9a066; }
  }
  * { box-sizing: border-box; }
  body {
    margin: 0; padding: 2.5rem 1.25rem; background: var(--bg); color: var(--fg);
    font: 15px/1.55 ui-sans-serif, system-ui, -apple-system, "Segoe UI", sans-serif;
  }
  main { max-width: 46rem; margin: 0 auto; }
  h1 { font-size: 1.5rem; margin: 0 0 .35rem; letter-spacing: -.01em; }
  p.lede { margin: 0 0 2rem; opacity: .72; }
  form { display: grid; gap: 1rem; padding: 1.25rem; border: 1px solid var(--line); border-radius: 10px; }
  label { display: block; font-weight: 600; font-size: .82rem; text-transform: uppercase;
          letter-spacing: .04em; opacity: .7; margin-bottom: .35rem; }
  input, select, button { font: inherit; color: inherit; }
  input[type=file] { width: 100%; }
  .row { display: flex; gap: 1rem; flex-wrap: wrap; }
  .row > div { flex: 1 1 12rem; }
  select, input[type=text] {
    width: 100%; padding: .5rem .6rem; background: transparent;
    border: 1px solid var(--line); border-radius: 6px;
  }
  button {
    justify-self: start; padding: .6rem 1.4rem; border: 0; border-radius: 6px;
    background: var(--accent); color: var(--bg); font-weight: 600; cursor: pointer;
  }
  button[disabled] { opacity: .5; cursor: progress; }
  #status { margin: 1.25rem 0 .5rem; min-height: 1.4em; opacity: .75; }
  pre {
    margin: 0; padding: 1rem; max-height: 26rem; overflow: auto; white-space: pre-wrap;
    word-break: break-word; border: 1px solid var(--line); border-radius: 10px;
    background: color-mix(in srgb, var(--fg) 5%, transparent); font-size: 13px;
  }
  code { font-size: .92em; }
  footer { margin-top: 2rem; font-size: .85rem; opacity: .6; }
</style>
</head>
<body>
<main>
  <h1>EasyOcrSharp &mdash; Web API sample</h1>
  <p class="lede">Pick an image and post it to <code>/ocr</code>. The very first request downloads the
     models, so it can take a minute; every request after that is fast.</p>

  <form id="f">
    <div>
      <label for="file">Image</label>
      <input id="file" type="file" name="file" accept="image/*" required>
    </div>
    <div class="row">
      <div>
        <label for="lang">Languages</label>
        <input id="lang" type="text" value="en" placeholder="en,fr">
      </div>
      <div>
        <label for="format">Format</label>
        <select id="format">
          <option value="json">json</option>
          <option value="text">text</option>
          <option value="hocr">hocr</option>
          <option value="alto">alto</option>
          <option value="tsv">tsv</option>
        </select>
      </div>
    </div>
    <button id="go" type="submit">Run OCR</button>
  </form>

  <p id="status"></p>
  <pre id="out">Results appear here.</pre>

  <footer>
    Also available: <code>POST /ocr/pdf</code> (upload a scanned PDF, get a searchable one back) and
    <code>GET /health</code>.
  </footer>
</main>
<script>
  const form = document.getElementById('f');
  const fileInput = document.getElementById('file');
  const out = document.getElementById('out');
  const statusEl = document.getElementById('status');
  const button = document.getElementById('go');

  form.addEventListener('submit', async (event) => {
    event.preventDefault();
    const file = fileInput.files[0];
    if (!file) return;

    const query = new URLSearchParams({
      lang: document.getElementById('lang').value.trim() || 'en',
      format: document.getElementById('format').value
    });
    const body = new FormData();
    body.append('file', file);

    button.disabled = true;
    statusEl.textContent = 'Running OCR on ' + file.name + '...';
    out.textContent = '';
    const started = performance.now();

    try {
      const response = await fetch('/ocr?' + query, { method: 'POST', body });
      const text = await response.text();
      const elapsed = Math.round(performance.now() - started);
      statusEl.textContent = 'HTTP ' + response.status + ' in ' + elapsed + ' ms';
      out.textContent = text || '(empty response)';
    } catch (error) {
      statusEl.textContent = 'Request failed';
      out.textContent = String(error);
    } finally {
      button.disabled = false;
    }
  });
</script>
</body>
</html>
""";
}
