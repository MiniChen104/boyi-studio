'use strict';

window.createEditorTools = function (editor, options) {
  const documents = new Map();
  const keywords = ('alignas alignof auto bool break case catch char class const constexpr continue decltype default delete do double else enum explicit extern false float for friend if inline int long namespace new noexcept nullptr operator private protected public register return short signed sizeof static static_assert struct switch template this thread_local throw true try typedef typeid typename union unsigned using virtual void volatile wchar_t while std cin cout endl string vector map set unordered_map unordered_set queue priority_queue stack pair sort reverse min max swap lower_bound upper_bound binary_search getline').split(' ');
  let hintTimer;
  let formatting = false;
  let hintsEnabled = true;
  const editable = () => !editor.getOption('readOnly') && options.current();
  const modify = command => cm => { if (editable()) command(cm); };

  function complete(cm) {
    const cursor = cm.getCursor();
    const token = cm.getTokenAt(cursor);
    if (/comment|string/.test(token.type || '')) return null;
    const prefix = cm.getLine(cursor.line).slice(0, cursor.ch).match(/[A-Za-z_][A-Za-z_0-9]*$/)?.[0] || '';
    if (!prefix) return null;
    const words = new Set(keywords);
    // Bound document scanning so completion remains responsive for large pastes.
    const start = Math.max(0, cursor.line - 300);
    const end = Math.min(cm.lastLine(), cursor.line + 300);
    for (let line = start; line <= end; line++) {
      for (const word of cm.getLine(line).slice(0, 2000).match(/[A-Za-z_][A-Za-z_0-9]*/g) || []) words.add(word);
    }
    const list = [...words].filter(word => word.startsWith(prefix) && word !== prefix).sort().slice(0, 60);
    return { list, from: CodeMirror.Pos(cursor.line, cursor.ch - prefix.length), to: cursor };
  }

  function showHints() {
    if (editable() && !editor.somethingSelected()) editor.showHint({ hint: complete, completeSingle: false });
  }

  function smartTab(cm) {
    if (cm.somethingSelected()) { cm.indentSelection('add'); return; }
    const size = cm.getOption('indentUnit');
    cm.replaceSelections(cm.listSelections().map(range => {
      const column = CodeMirror.countColumn(cm.getLine(range.head.line), range.head.ch, cm.getOption('tabSize'));
      return ' '.repeat(size - column % size);
    }), 'end', '+input');
  }

  async function format() {
    if (!editable() || formatting) return;
    const doc = editor.getDoc();
    const item = options.current();
    const original = doc.getValue();
    const cursor = doc.indexFromPos(doc.getCursor());
    const generation = doc.changeGeneration();
    formatting = true;
    try {
      await options.save();
      const result = await options.rpc('formatCode', { code: original, cursor });
      if (editor.getDoc() !== doc || options.current() !== item || !editable() || !doc.isClean(generation)) {
        options.toast('代码已变化，请重新整理当前版本。');
        return;
      }
      if (result.code === original) { options.toast('代码格式已经整齐。'); return; }
      editor.operation(() => {
        doc.replaceRange(result.code, CodeMirror.Pos(0, 0), CodeMirror.Pos(doc.lastLine(), doc.getLine(doc.lastLine()).length), 'format');
        doc.setCursor(doc.posFromIndex(result.cursor));
      });
      doc.changeGeneration();
      await options.save(false, '格式化前备份');
      options.toast('已整理代码，可按 Ctrl+Z 撤销。', 'success');
    } catch (error) {
      options.toast(error.message || String(error), 'error');
    } finally {
      formatting = false;
    }
  }

  const keys = {
    ...editor.getOption('extraKeys'),
    Tab: modify(smartTab),
    'Shift-Tab': modify(cm => cm.indentSelection('subtract')),
    'Ctrl-/': modify(cm => cm.toggleComment({ indent: true })),
    'Ctrl-Alt-L': format,
    'Ctrl-Space': showHints,
    'Ctrl-F': 'findPersistent',
    'Ctrl-H': modify(cm => cm.execCommand('replace')),
    'Ctrl-Shift-H': modify(cm => cm.execCommand('replaceAll')),
    'F3': 'findNext',
    'Shift-F3': 'findPrev',
    'Alt-G': 'jumpToLine',
    'Ctrl-Shift-D': modify(cm => {
      const cursor = cm.getCursor();
      if (cm.somethingSelected()) {
        cm.replaceSelections(cm.getSelections().map(text => text.repeat(2)), 'around', 'duplicate');
      } else {
        cm.replaceRange('\n' + cm.getLine(cursor.line), CodeMirror.Pos(cursor.line, cm.getLine(cursor.line).length), undefined, 'duplicate');
        cm.setCursor(cursor.line + 1, cursor.ch);
      }
    })
  };
  editor.setOption('extraKeys', keys);
  editor.setOption('smartIndent', true);
  editor.setOption('indentWithTabs', false);
  editor.setOption('foldGutter', true);
  editor.setOption('gutters', ['CodeMirror-linenumbers', 'CodeMirror-foldgutter']);
  editor.setOption('highlightSelectionMatches', { minChars: 2, showToken: /\w/, delay: 150 });
  editor.setOption('phrases', {
    'Search:': '查找', 'Replace:': '替换', 'Replace all:': '全部替换',
    'With:': '替换为', 'Replace?': '替换此处？', 'Yes': '替换', 'No': '跳过', 'All': '全部', 'Stop': '结束',
    '(Use /re/ syntax for regexp search)': 'Enter 下一处 · Shift+Enter 上一处 · Esc 关闭 · 支持 /正则/',
    'Jump to line:': '跳转到行', '(Use line:column or scroll% syntax)': '输入行号或 行:列'
  });
  editor.on('inputRead', (cm, change) => {
    clearTimeout(hintTimer);
    if (!hintsEnabled || change.origin !== '+input' || !/^[A-Za-z_][A-Za-z_0-9]*$/.test(change.text.join(''))) return;
    hintTimer = setTimeout(() => {
      if (cm.hasFocus() && /[A-Za-z_][A-Za-z_0-9]+$/.test(cm.getLine(cm.getCursor().line).slice(0, cm.getCursor().ch))) showHints();
    }, 220);
  });
  editor.on('blur', () => clearTimeout(hintTimer));

  function cancelTransient() {
    clearTimeout(hintTimer);
    editor.closeHint();
    editor.execCommand('clearSearch');
    editor.getWrapperElement().querySelectorAll('.CodeMirror-dialog').forEach(dialog => {
      const input = dialog.querySelector('input');
      if (input) input.dispatchEvent(new KeyboardEvent('keydown', { keyCode: 27, bubbles: true }));
      else dialog.querySelector('button:last-child')?.click();
    });
  }

  return {
    format,
    hints: showHints,
    apply(settings) {
      const size = [2, 4, 8].includes(settings.indentSize) ? settings.indentSize : 4;
      for (const [name, value] of Object.entries({ indentUnit: size, tabSize: size, autoCloseBrackets: settings.autoBrackets !== false })) {
        if (editor.getOption(name) !== value) editor.setOption(name, value);
      }
      hintsEnabled = settings.codeHints !== false;
      if (!hintsEnabled) { clearTimeout(hintTimer); editor.closeHint(); }
    },
    open(key, value, readOnly) {
      cancelTransient();
      let doc = documents.get(key);
      if (!doc) doc = new CodeMirror.Doc(value, 'text/x-c++src');
      else if (doc.getValue() !== value) {
        doc.replaceRange(value, CodeMirror.Pos(0, 0), CodeMirror.Pos(doc.lastLine(), doc.getLine(doc.lastLine()).length), 'restore');
      }
      documents.delete(key);
      documents.set(key, doc);
      if (documents.size > 24) documents.delete(documents.keys().next().value);
      if (editor.getDoc() !== doc) editor.swapDoc(doc);
      editor.setOption('readOnly', readOnly);
    },
    clear() {
      cancelTransient();
      documents.clear();
      editor.swapDoc(new CodeMirror.Doc('', 'text/x-c++src'));
      editor.setOption('readOnly', true);
    }
  };
};
