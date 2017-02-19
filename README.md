# The Closer

This is a utility that, when executed, closes the window or tab currently under the mouse cursor, even if the window is not active (i.e. does not have focus). Multiple methods of closing a window are supported and can be configured per application via app.config settings. The default behavior is CTRL-W.

## Supported methods

- Keyboard: ESCAPE
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
    &lt;add key="devenv" value="CTRL-F4" /&gt;
    &lt;add key="notepad" value="WM_QUIT" /&gt;
    &lt;add key="pageant" value="ESCAPE" /&gt;
  &lt;/appSettings&gt;
&lt;/configuration&gt;
</code></pre>
