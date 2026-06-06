// Y# language definition for the Monaco editor.
//
// Usage:
//   import { language, conf } from './ysharp.js';
//   monaco.languages.register({ id: 'ysharp', extensions: ['.yas'] });
//   monaco.languages.setMonarchTokensProvider('ysharp', language);
//   monaco.languages.setLanguageConfiguration('ysharp', conf);

export const conf = {
    comments: { lineComment: '//' },
    brackets: [['{', '}'], ['[', ']'], ['(', ')']],
    autoClosingPairs: [
        { open: '{', close: '}' },
        { open: '[', close: ']' },
        { open: '(', close: ')' },
        { open: '"', close: '"', notIn: ['string'] },
        { open: '$"', close: '"', notIn: ['string'] },
    ],
    surroundingPairs: [
        { open: '{', close: '}' },
        { open: '[', close: ']' },
        { open: '(', close: ')' },
        { open: '"', close: '"' },
    ],
};

export const language = {
    defaultToken: '',
    tokenPostfix: '.ysharp',

    keywords: [
        'fn', 'record', 'class', 'interface', 'enum', 'error', 'this',
        'return', 'if', 'else', 'let', 'mut', 'match',
        'for', 'in', 'break', 'continue', 'with',
        'blocking', 'concurrent', 'scope',
        'service', 'singleton', 'scoped', 'transient',
        'module', 'bind', 'provide', 'app', 'extends',
        'route', 'get', 'post', 'put', 'delete', 'modifier',
    ],

    typeKeywords: [
        'int', 'long', 'float', 'double', 'bool', 'string', 'void',
        'Result', 'Option', 'List', 'Error',
    ],

    constants: ['true', 'false', 'None', 'Some'],

    operators: [
        '=', '==', '!=', '<', '>', '<=', '>=', '&&', '||', '!',
        '+', '-', '*', '/', '%', '+=', '-=', '*=', '/=', '%=',
        '->', '=>', '?', '..',
    ],

    symbols: /[=><!~?:&|+\-*\/\^%]+/,

    tokenizer: {
        root: [
            // Modifiers: ~name
            [/~[a-zA-Z_][\w]*/, 'annotation'],

            // Identifiers / keywords
            [/[A-Z][\w]*/, {
                cases: {
                    '@typeKeywords': 'type',
                    '@default': 'type.identifier',
                },
            }],
            [/[a-z_][\w]*/, {
                cases: {
                    '@keywords': 'keyword',
                    '@typeKeywords': 'type',
                    '@constants': 'constant',
                    '@default': 'identifier',
                },
            }],

            // Whitespace
            { include: '@whitespace' },

            // Numbers
            [/\d*\.\d+([eE][\-+]?\d+)?/, 'number.float'],
            [/\d+/, 'number'],

            // Delimiters and operators
            [/[{}()\[\]]/, '@brackets'],
            [/[<>](?!@symbols)/, '@brackets'],
            [/@symbols/, { cases: { '@operators': 'operator', '@default': '' } }],
            [/[;,.]/, 'delimiter'],

            // Interpolated strings
            [/\$"/, { token: 'string.quote', next: '@interpString' }],

            // Plain strings
            [/"/, { token: 'string.quote', next: '@string' }],
        ],

        whitespace: [
            [/[ \t\r\n]+/, ''],
            [/\/\/.*$/, 'comment'],
        ],

        string: [
            [/[^\\"]+/, 'string'],
            [/\\./, 'string.escape'],
            [/"/, { token: 'string.quote', next: '@pop' }],
        ],

        interpString: [
            [/[^\\"{]+/, 'string'],
            [/\\./, 'string.escape'],
            [/\{/, { token: 'delimiter.bracket', next: '@interpExpr' }],
            [/"/, { token: 'string.quote', next: '@pop' }],
        ],

        interpExpr: [
            [/\}/, { token: 'delimiter.bracket', next: '@pop' }],
            { include: 'root' },
        ],
    },
};
