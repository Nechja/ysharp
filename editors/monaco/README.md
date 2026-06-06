# Y# for Monaco

Drop-in Y# language support for any Monaco-based editor.

```js
import * as monaco from 'monaco-editor';
import { language, conf } from './ysharp.js';

monaco.languages.register({ id: 'ysharp', extensions: ['.yas'], aliases: ['Y#', 'ysharp'] });
monaco.languages.setMonarchTokensProvider('ysharp', language);
monaco.languages.setLanguageConfiguration('ysharp', conf);
```

That gives you keyword/type/operator coloring, `~modifier` highlighting, interpolated strings (`$"...{ expr }..."`), bracket matching, and `//` line comments. No build step.

For diagnostics and document symbols, point a `monaco-languageclient` instance at the `ysharp-lsp` binary.
