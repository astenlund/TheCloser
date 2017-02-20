# The Closer

This is a utility that, when executed, closes the window at the current mouse cursor position. Multiple methods of closing a window are supported and this behavior is configurable per application via app.config settings.

## Supported methods

- Keyboard: CTRL-W
- Keyboard: CTRL-F4
- Windows Message: WM_DESTROY
- Windows Message: WM_CLOSE
- Windows Message: WM_QUIT

## Example app.config

<pre><code class="language-xml">
&lt;?xml version="1.0" encoding="utf-8" ?&gt;
&lt;configuration&gt;
  &lt;startup&gt;
    &lt;supportedRuntime version="v4.0" sku=".NETFramework,Version=v4.5.2" /&gt;
  &lt;/startup&gt;
  &lt;appSettings&gt;
    &lt;add key="chrome" value="CTRL-W" /&gt;
    &lt;add key="sublime_text" value="CTRL-W" /&gt;
    &lt;add key="explorer" value="CTRL-W"/&gt;
    &lt;add key="notepad" value="WM_QUIT"/&gt;
  &lt;/appSettings&gt;
&lt;/configuration&gt;
</code></pre>
