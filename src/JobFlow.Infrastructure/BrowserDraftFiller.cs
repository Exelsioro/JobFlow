using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace JobFlow.Infrastructure;

public static class BrowserDraftFiller
{
    public sealed record Field(int Index, string Kind, string Label, string InputName, string InputType);
    public sealed record Form(int Index, Field[] Fields);
    public sealed record Preview(string Url, Form[] Forms);

    public static async Task<Preview> InspectAsync(IPage page)
    {
        if (!Uri.TryCreate(page.Url, UriKind.Absolute, out var url) ||
            !(url.Scheme == "https" || (url.Scheme == "http" && url.IsLoopback)))
            throw new InvalidOperationException("Navigate to the application form over HTTPS.");
        var forms = page.Locator("form");
        var results = new List<Form>();
        for (var index = 0; index < Math.Min(await forms.CountAsync(), 30); index++)
        {
            var controls = Controls(forms.Nth(index));
            var fields = new List<Field>();
            for (var k = 0; k < Math.Min(await controls.CountAsync(), 80); k++)
            {
                var control = controls.Nth(k);
                if (!await control.IsVisibleAsync()) continue;
                var metadata = await control.EvaluateAsync<string[]>("""
                    el => [el.type, el.autocomplete || '', el.name || '',
                           el.getAttribute('aria-label') || Array.from(el.labels || [], l => l.textContent).join(' ')]
                    """);
                var kind = Identify(metadata);
                if (kind != "" && (kind == "resume" || await control.IsEditableAsync()))
                {
                    var label = metadata[3].Trim();
                    fields.Add(new(k, kind, label[..Math.Min(label.Length, 120)], metadata[2], metadata[0]));
                }
            }
            var unique = fields.GroupBy(x => x.Kind).Where(group => group.Count() == 1).Select(group => group.Single()).ToArray();
            // Require a signal specific to an application. Email-only forms could be login or newsletter forms.
            if (unique.Any(x => x.Kind is "letter" or "resume") && unique.Any(x => x.Kind != "resume"))
                results.Add(new(index, unique));
        }
        return new(page.Url, results.ToArray());
    }

    public static async Task<int> FillAsync(IPage page, Preview expected, int formIndex, IReadOnlyDictionary<string, string> values)
    {
        var current = await InspectAsync(page);
        var form = current.Forms.SingleOrDefault(x => x.Index == formIndex);
        var previous = expected.Forms.SingleOrDefault(x => x.Index == formIndex);
        if (current.Url != expected.Url || form == null || previous == null || !form.Fields.SequenceEqual(previous.Fields))
            throw new InvalidOperationException("The browser form changed. Inspect it again.");
        var source = new Uri(current.Url);
        var controls = Controls(page.Locator("form").Nth(form.Index));
        var filled = 0;
        Action<IRoute> guard = async route =>
        {
            if (route.Request.IsNavigationRequest || route.Request.Method is not ("GET" or "HEAD"))
                await route.AbortAsync();
            else
                await route.FallbackAsync();
        };
        await page.RouteAsync("**/*", guard);
        try
        {
            foreach (var field in form.Fields)
            {
                if (!values.TryGetValue(field.Kind, out var value) || string.IsNullOrWhiteSpace(value)) continue;
                if (page.Url != current.Url) throw new InvalidOperationException("The browser navigated during filling.");
                var control = controls.Nth(field.Index);
                if (!string.IsNullOrEmpty(await control.InputValueAsync(new() { Timeout = 3000 }))) continue;
                // Check the origin and field again in the same page operation that writes the value.
                var applied = await control.EvaluateAsync<bool>("""
                    (el, data) => {
                      if (location.href !== data.url || location.origin !== data.origin || el.value || el.disabled || el.readOnly) return false;
                      const label = (el.getAttribute('aria-label') || Array.from(el.labels || [], l => l.textContent).join(' ')).trim().slice(0,120);
                      if (label !== data.label || el.name !== data.inputName || el.type !== data.inputType || !el.getClientRects().length) return false;
                      if (!(el instanceof HTMLTextAreaElement) && !(el instanceof HTMLInputElement && ['text','email','tel'].includes(el.type))) return false;
                      const proto = el instanceof HTMLTextAreaElement ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
                      Object.getOwnPropertyDescriptor(proto, 'value').set.call(el, data.value);
                      el.dispatchEvent(new Event('input', { bubbles: true }));
                      el.dispatchEvent(new Event('change', { bubbles: true }));
                      return true;
                    }
                    """, new { url = current.Url, origin = source.GetLeftPart(UriPartial.Authority), label = field.Label, inputName = field.InputName, inputType = field.InputType, value });
                if (applied) filled++;
            }
            return filled;
        }
        finally { if (!page.IsClosed) await page.UnrouteAsync("**/*", guard); }
    }

    private static ILocator Controls(ILocator form) => form.Locator("textarea, input:not([type]), input[type=text], input[type=email], input[type=tel], input[type=file]");

    private static string Identify(string[] info)
    {
        var names = Regex.Replace(info[2], "([a-z])([A-Z])", "$1 $2").Replace('_', ' ').Replace('-', ' ');
        var text = (names + " " + info[3]).ToLowerInvariant();
        if (info[0] == "file") return Regex.IsMatch(text, @"\bresume\b|\bcv\b|резюме") ? "resume" : "";
        if (info[0] == "textarea" && Regex.IsMatch(text, @"\bcover\s*letter\b|сопроводительн")) return "letter";
        if (info[0] == "email" || info[1] == "email" || Regex.IsMatch(text, @"\be[ -]?mail\b|электронная почта")) return "email";
        if (info[1] == "name" || Regex.IsMatch(text, @"\bfull\s*name\b|фио|полное имя")) return "name";
        return "";
    }
}
