using System.Net;

namespace TrackMeBaby.Ui;

/// <summary>
/// The one place colour, type and spacing are decided, shared by the live pages and by exported
/// files so a report looks like the dashboard it came from.
///
/// Colours come from the validated reference palette. Data marks carry their light-mode value as a
/// presentation attribute *and* a class that repaints them for dark mode: the attribute is what
/// survives when an SVG is pulled out of the page and rasterised to PNG, which is why marks are
/// never painted by CSS custom property alone.
/// </summary>
public static class Theme
{
    /// <summary>Categorical slots, in fixed order. A series keeps its slot as the data changes.</summary>
    public static readonly string[] Series = ["#2a78d6", "#eb6834", "#1baf7a", "#eda100", "#e87ba4", "#008300"];

    /// <summary>Blue ramp for magnitude — heatmap cells and nothing else.</summary>
    public static readonly string[] Sequential = ["#cde2fb", "#9ec5f4", "#6da7ec", "#3987e5", "#256abf", "#104281"];

    public static string SeriesClass(int slot) => $"s{(slot % Series.Length) + 1}";
    public static string SeriesColor(int slot) => Series[slot % Series.Length];

    public static string Escape(string? value) => WebUtility.HtmlEncode(value ?? "");

    /// <summary>
    /// Escaping for text that will land inside a data-tip attribute and then be written with
    /// innerHTML. The attribute is decoded once on the way out, so a single encoding would leave a
    /// pull request title of &lt;img onerror=...&gt; live in the tooltip. Encoded twice, it renders
    /// as the characters it is.
    /// </summary>
    public static string EscapeTip(string? value) => WebUtility.HtmlEncode(WebUtility.HtmlEncode(value ?? ""));

    public static string Fmt(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>Compact figures for tiles: 1,284 / 12.9K / 4.2M.</summary>
    public static string Compact(double value) => Math.Abs(value) switch
    {
        >= 1_000_000 => $"{value / 1_000_000:0.#}M",
        >= 10_000 => $"{value / 1_000:0.#}K",
        _ => value.ToString("N0", CultureInfo.InvariantCulture)
    };

    public static string Percent(double? ratio) => ratio is null ? "—" : $"{ratio.Value * 100:0.#}%";

    /// <summary>
    /// A status pill. The word is always present, so the colour never carries the meaning alone —
    /// which is the whole reason status colours are allowed here at all.
    /// </summary>
    public static string Pill(string label, string tone, string glyph = "") =>
        $"<span class=\"pill {tone}\">{(glyph.Length > 0 ? $"<span aria-hidden=\"true\">{glyph}</span> " : "")}{Escape(label)}</span>";

    public static (string Tone, string Glyph) BandTone(Services.DoraBand band) => band switch
    {
        Services.DoraBand.Elite => ("good", "&#9650;"),
        Services.DoraBand.High => ("good", "&#9650;"),
        Services.DoraBand.Medium => ("warning", "&#9679;"),
        Services.DoraBand.Low => ("critical", "&#9660;"),
        _ => ("idle", "&#8211;")
    };

    /// <summary>
    /// Hover tooltips and PNG download. Small enough to inline everywhere, and everything it
    /// enhances is already readable without it — every chart ships a table view.
    /// </summary>
    public const string Script = """
      (function () {
        var tip = document.createElement('div');
        tip.className = 'tip';
        tip.setAttribute('role', 'status');
        document.body.appendChild(tip);

        function show(target, event) {
          var text = target.getAttribute('data-tip');
          if (!text) return;
          tip.innerHTML = text;
          tip.classList.add('on');
          var pad = 14;
          var box = tip.getBoundingClientRect();
          var x = event.clientX + pad;
          var y = event.clientY + pad;
          if (x + box.width > window.innerWidth - 8) x = event.clientX - box.width - pad;
          if (y + box.height > window.innerHeight - 8) y = event.clientY - box.height - pad;
          tip.style.left = x + 'px';
          tip.style.top = y + 'px';
        }

        function hide() { tip.classList.remove('on'); }

        document.addEventListener('mousemove', function (event) {
          var target = event.target.closest ? event.target.closest('[data-tip]') : null;
          if (target) show(target, event); else hide();
        });
        document.addEventListener('mouseleave', hide);

        // PNG from the chart's own SVG. The marks carry presentation attributes, so the
        // serialised copy renders standalone -- no stylesheet needed, always light-mode,
        // which is what a slide or a document wants.
        document.addEventListener('click', function (event) {
          var button = event.target.closest ? event.target.closest('[data-png]') : null;
          if (!button) return;
          var figure = button.closest('figure') || button.parentElement;
          var svg = figure ? figure.querySelector('svg') : null;
          if (!svg) return;

          var clone = svg.cloneNode(true);
          clone.setAttribute('xmlns', 'http://www.w3.org/2000/svg');
          var width = svg.viewBox.baseVal.width || svg.clientWidth || 900;
          var height = svg.viewBox.baseVal.height || svg.clientHeight || 300;
          var background = document.createElementNS('http://www.w3.org/2000/svg', 'rect');
          background.setAttribute('width', width);
          background.setAttribute('height', height);
          background.setAttribute('fill', '#fcfcfb');
          clone.insertBefore(background, clone.firstChild);

          var scale = 2;
          var blob = new Blob([new XMLSerializer().serializeToString(clone)], {type: 'image/svg+xml'});
          var url = URL.createObjectURL(blob);
          var image = new Image();
          image.onload = function () {
            var canvas = document.createElement('canvas');
            canvas.width = width * scale;
            canvas.height = height * scale;
            var context = canvas.getContext('2d');
            context.scale(scale, scale);
            context.drawImage(image, 0, 0);
            URL.revokeObjectURL(url);
            canvas.toBlob(function (png) {
              var link = document.createElement('a');
              link.href = URL.createObjectURL(png);
              link.download = (button.getAttribute('data-png') || 'chart') + '.png';
              link.click();
              setTimeout(function () { URL.revokeObjectURL(link.href); }, 1000);
            });
          };
          image.src = url;
        });
      })();
      """;

    public const string Css = """
      :root {
        color-scheme: light;
        --plane: #f9f9f7; --surface: #fcfcfb;
        --ink: #0b0b0b; --ink-2: #52514e; --muted: #898781;
        --hairline: rgba(11,11,11,0.10); --grid: #e1e0d9; --track: #eceae4;
        --axis: #c3c2b7;
        --series: #2a78d6;
        --good: #0ca30c; --warning: #fab219; --critical: #d03b3b; --idle: #898781;
        --good-ink: #006300; --critical-ink: #b02a2a;
      }
      @media (prefers-color-scheme: dark) {
        :root {
          color-scheme: dark;
          --plane: #0d0d0d; --surface: #1a1a19;
          --ink: #ffffff; --ink-2: #c3c2b7; --muted: #898781;
          --hairline: rgba(255,255,255,0.10); --grid: #2c2c2a; --track: #2c2c2a;
          --axis: #383835;
          --series: #3987e5;
          --good-ink: #0ca30c; --critical-ink: #e66767;
        }
      }
      * { box-sizing: border-box; }
      body { margin: 0; padding: 40px 24px 72px;
             font: 15px/1.55 system-ui, -apple-system, "Segoe UI", sans-serif;
             background: var(--plane); color: var(--ink); }
      main { max-width: 1120px; margin: 0 auto; }
      a { color: inherit; text-decoration-color: var(--muted); text-underline-offset: 2px; }

      h1 { font-size: 21px; margin: 0 0 6px; letter-spacing: -0.01em; }
      h2 { font-size: 12px; margin: 34px 0 10px; text-transform: uppercase;
           letter-spacing: .08em; color: var(--ink-2); font-weight: 600; }
      h3 { font-size: 14px; margin: 0 0 2px; font-weight: 600; }
      .sub { margin: 0; color: var(--ink-2); font-size: 13.5px; }
      .muted { color: var(--muted); font-weight: 400; }
      .ok { color: var(--good-ink); font-weight: 600; }
      .bad { color: var(--critical-ink); font-weight: 600; }
      code { background: var(--track); padding: 1px 6px; border-radius: 5px;
             font-size: 12.5px; font-family: ui-monospace, "Cascadia Mono", Consolas, monospace; }

      .head { display: flex; align-items: flex-start; justify-content: space-between;
              gap: 20px; flex-wrap: wrap; margin-bottom: 22px; }
      button, .btn { font: inherit; font-size: 13.5px; font-weight: 600; padding: 8px 15px;
               border-radius: 8px; border: 1px solid var(--hairline); background: var(--surface);
               color: var(--ink); cursor: pointer; text-decoration: none; display: inline-block; }
      button:hover, .btn:hover { border-color: var(--muted); }
      .actions { display: flex; gap: 8px; flex-wrap: wrap; align-items: center; }

      .pill { display: inline-flex; align-items: center; gap: 5px; vertical-align: 1px;
              padding: 2px 10px; border-radius: 999px; font-size: 11px; font-weight: 700;
              letter-spacing: .04em; text-transform: uppercase; color: #fff; white-space: nowrap; }
      .pill.good { background: var(--good); } .pill.warning { background: var(--warning); color: #2b2100; }
      .pill.critical { background: var(--critical); } .pill.idle { background: var(--idle); }

      .card { background: var(--surface); border: 1px solid var(--hairline);
              border-radius: 12px; padding: 18px 20px; }
      .note { margin: 14px 0 0; padding: 12px 16px; border-radius: 10px;
              border: 1px solid var(--hairline); color: var(--ink-2); font-size: 13px;
              background: var(--surface); }
      .note.critical { border-color: var(--critical); color: var(--critical-ink); }
      .caveats { margin: 10px 0 0; padding-left: 18px; font-size: 12.5px; color: var(--muted); }
      .caveats li { margin: 3px 0; }

      .grid { display: grid; gap: 14px; }
      .two { grid-template-columns: 1fr 1fr; align-items: start; }
      .three { grid-template-columns: repeat(3, 1fr); }
      .four { grid-template-columns: repeat(4, 1fr); }
      .hero-row { grid-template-columns: minmax(240px, 1fr) 2.2fr; align-items: stretch; }
      @media (max-width: 900px) {
        .two, .three, .four, .hero-row { grid-template-columns: 1fr 1fr; }
      }
      @media (max-width: 620px) { .two, .three, .four, .hero-row { grid-template-columns: 1fr; } }

      .label { margin: 0; font-size: 12px; color: var(--ink-2); }
      .hero { display: flex; flex-direction: column; justify-content: center; }
      .figure { margin: 6px 0 4px; font-size: 52px; line-height: 1; font-weight: 600;
                letter-spacing: -0.02em; }
      .tile { padding: 14px 16px; display: flex; flex-direction: column; gap: 2px; }
      .tile-value { margin: 4px 0 0; font-size: 26px; font-weight: 600; line-height: 1.1; }
      .tile-foot { margin: 6px 0 0; font-size: 12px; color: var(--muted); }
      .tile-head { display: flex; justify-content: space-between; align-items: baseline; gap: 8px; }

      .facts { display: grid; grid-template-columns: repeat(auto-fit, minmax(190px, 1fr));
               gap: 16px 24px; margin: 0; }
      dt { font-size: 12px; color: var(--ink-2); margin-bottom: 2px; }
      dd { margin: 0; font-size: 14.5px; font-variant-numeric: tabular-nums; }

      figure { margin: 0; }
      figcaption { display: flex; justify-content: space-between; align-items: baseline;
                   gap: 12px; margin-bottom: 10px; }
      figcaption .cap { font-size: 13px; color: var(--ink-2); }
      svg { display: block; width: 100%; height: auto; overflow: visible; }
      .png { font-size: 11px; padding: 3px 9px; font-weight: 600; color: var(--muted);
             background: none; border-color: transparent; }
      .png:hover { color: var(--ink); border-color: var(--hairline); }

      /* Chart ink. Marks carry light values as attributes; these repaint them for dark mode. */
      .axis-line { stroke: var(--axis); stroke-width: 1; }
      .grid-line { stroke: var(--grid); stroke-width: 1; }
      .tick { fill: var(--muted); font-size: 11px; }
      .tick.num { font-variant-numeric: tabular-nums; }
      .mark-label { fill: var(--ink); font-size: 11.5px; font-weight: 600; }
      .mark-label.dim { fill: var(--muted); font-weight: 400; }
      .track-fill { fill: var(--track); }
      @media (prefers-color-scheme: dark) {
        .s1 { fill: #3987e5; } .s2 { fill: #d95926; } .s3 { fill: #199e70; }
        .s4 { fill: #c98500; } .s5 { fill: #d55181; } .s6 { fill: #008300; }
        .s1-stroke { stroke: #3987e5; } .s2-stroke { stroke: #d95926; }
        .s3-stroke { stroke: #199e70; } .s4-stroke { stroke: #c98500; }
        .s5-stroke { stroke: #d55181; } .s6-stroke { stroke: #008300; }
        .s1-bg { background: #3987e5 !important; } .s2-bg { background: #d95926 !important; }
        .s3-bg { background: #199e70 !important; } .s4-bg { background: #c98500 !important; }
        .s5-bg { background: #d55181 !important; } .s6-bg { background: #008300 !important; }
        .cell-0 { fill: var(--track); }
      }
      .hit { fill: transparent; }
      .hit:hover { fill: var(--ink); fill-opacity: .04; }

      .legend { display: flex; flex-wrap: wrap; gap: 12px; margin-top: 12px; }
      .key { display: inline-flex; align-items: center; gap: 6px; font-size: 12.5px; color: var(--ink-2); }
      .swatch { width: 10px; height: 10px; border-radius: 3px; display: inline-block; }

      .bars { display: flex; flex-direction: column; gap: 11px; }
      .bar-row { display: grid; grid-template-columns: 150px 1fr 62px; align-items: center; gap: 12px; }
      .bar-label { font-size: 13px; color: var(--ink-2); overflow: hidden;
                   text-overflow: ellipsis; white-space: nowrap; }
      .bar-track { background: var(--track); border-radius: 5px; height: 10px; }
      .bar-fill { display: block; height: 100%; background: var(--series);
                  border-radius: 0 4px 4px 0; }
      .bar-value { font-size: 13px; font-variant-numeric: tabular-nums; text-align: right; }

      .stack { display: flex; height: 22px; border-radius: 5px; overflow: hidden; gap: 2px;
               background: var(--surface); }
      .stack span { display: block; height: 100%; }

      .table-view { margin-top: 14px; border-top: 1px solid var(--grid); padding-top: 10px; }
      .table-view summary { font-size: 12.5px; color: var(--ink-2); cursor: pointer; }
      .table-view table { margin-top: 8px; }
      table { width: 100%; border-collapse: collapse; }
      td, th { text-align: left; padding: 8px 10px 8px 0; border-bottom: 1px solid var(--grid);
               font-size: 13.5px; }
      th { color: var(--ink-2); font-weight: 600; font-size: 12px; }
      tr:last-child td { border-bottom: none; }
      .num { text-align: right; font-variant-numeric: tabular-nums; }
      .nowrap { white-space: nowrap; }

      .tabs { display: flex; gap: 6px; flex-wrap: wrap; margin: 0 0 4px; }
      .tab { font-size: 12.5px; padding: 5px 11px; border-radius: 999px; text-decoration: none;
             color: var(--ink-2); border: 1px solid var(--hairline); background: var(--surface); }
      .tab.on { background: var(--ink); color: var(--plane); border-color: var(--ink); font-weight: 600; }

      .env { padding: 18px 0; border-bottom: 1px solid var(--grid); }
      .env:first-of-type { padding-top: 0; }
      .env:last-of-type { border-bottom: none; padding-bottom: 0; }
      .env-head { display: flex; align-items: baseline; gap: 10px; flex-wrap: wrap;
                  margin-bottom: 14px; }
      .env-name { font-size: 16px; font-weight: 600; }

      .tip { position: fixed; z-index: 40; pointer-events: none; opacity: 0;
             transition: opacity .08s; background: var(--ink); color: var(--plane);
             padding: 7px 10px; border-radius: 8px; font-size: 12px; line-height: 1.45;
             max-width: 280px; box-shadow: 0 6px 20px rgba(0,0,0,.18); }
      .tip.on { opacity: 1; }
      .tip b { font-weight: 600; }

      .cover { padding: 0 0 26px; border-bottom: 1px solid var(--grid); margin-bottom: 10px; }
      .cover h1 { font-size: 27px; }
      .cover .sub { font-size: 14.5px; }
      .prose { font-size: 14.5px; line-height: 1.6; }
      .prose h3 { margin-top: 18px; }

      @media print {
        body { padding: 0; background: #fff; }
        .no-print { display: none !important; }
        .card { break-inside: avoid; border-color: #ddd; }
        .env, figure, table { break-inside: avoid; }
        h2 { break-after: avoid; }
        a { text-decoration: none; }
      }
      """;
}
