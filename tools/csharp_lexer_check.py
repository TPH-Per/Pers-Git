#!/usr/bin/env python3
r"""
Strict C# 12 Lexer & Syntax Verifier (`tools/csharp_lexer_check.py`).
Audits every `.cs` file in the solution to guarantee:
1. Zero raw newline or control characters (< 0x20 except \t) inside regular `"..."` string literals or `'...'` char literals.
2. Valid escape sequences inside regular string and char literals (`\\`, `\"`, `\'`, `\n`, `\r`, `\t`, `\0`, `\x..`, `\u....`).
3. Exactly 1 character (or 1 valid escape sequence) inside every `'...'` character literal.
4. Balanced braces `{}`, parentheses `()`, and brackets `[]` outside strings/comments.
5. Zero double-unescaped artifacts (e.g. 0x08 backspace or 0x00 NUL bytes in source text).
"""
import os
import sys

VALID_SIMPLE_ESCAPES = set(['\\', '"', "'", 'n', 'r', 't', '0', 'a', 'b', 'f', 'v'])

def verify_cs_file(filepath: str) -> list[str]:
    errors = []
    with open(filepath, "rb") as fb:
        raw_bytes = fb.read()

    for idx, b in enumerate(raw_bytes):
        if b < 0x20 and b not in (0x09, 0x0A, 0x0D):
            errors.append(f"{filepath}: Byte offset {idx} contains raw control character 0x{b:02X}")

    try:
        text = raw_bytes.decode("utf-8")
    except UnicodeDecodeError as e:
        errors.append(f"{filepath}: Invalid UTF-8 encoding: {e}")
        return errors

    n = len(text)
    i = 0
    line = 1
    col = 1

    brace_stack = []

    def advance(count=1):
        nonlocal i, line, col
        for _ in range(count):
            if i < n:
                if text[i] == '\n':
                    line += 1
                    col = 1
                else:
                    col += 1
                i += 1

    while i < n:
        ch = text[i]

        if ch == '/' and i + 1 < n and text[i + 1] == '/':
            while i < n and text[i] != '\n':
                advance()
            continue

        if ch == '/' and i + 1 < n and text[i + 1] == '*':
            start_line = line
            advance(2)
            while i + 1 < n and not (text[i] == '*' and text[i + 1] == '/'):
                advance()
            if i + 1 >= n:
                errors.append(f"{filepath}:{start_line}: Unterminated block comment")
                break
            advance(2)
            continue

        if ch in ('$', '@', '"'):
            p = i
            dollars = 0
            ats = 0
            while p < n and text[p] in ('$', '@'):
                if text[p] == '$':
                    dollars += 1
                else:
                    ats += 1
                p += 1

            if p < n and text[p] == '"':
                q_count = 0
                while p + q_count < n and text[p + q_count] == '"':
                    q_count += 1

                if q_count >= 3:
                    start_line, start_col = line, col
                    advance((p - i) + q_count)
                    closing_quotes = '"' * q_count
                    found_close = False
                    while i < n:
                        if text.startswith(closing_quotes, i):
                            advance(q_count)
                            found_close = True
                            break
                        advance()
                    if not found_close:
                        errors.append(f"{filepath}:{start_line}:{start_col}: Unterminated raw string literal")
                    continue

                elif ats > 0:
                    start_line, start_col = line, col
                    advance((p - i) + 1)
                    closed = False
                    while i < n:
                        if text[i] == '"':
                            if i + 1 < n and text[i + 1] == '"':
                                advance(2)
                                continue
                            advance()
                            closed = True
                            break
                        advance()
                    if not closed:
                        errors.append(f"{filepath}:{start_line}:{start_col}: Unterminated verbatim string literal")
                    continue

                else:
                    start_line, start_col = line, col
                    is_interpolated = dollars > 0
                    advance((p - i) + 1)
                    closed = False
                    interp_brace_depth = 0
                    while i < n:
                        c = text[i]
                        if c == '\n' or c == '\r':
                            errors.append(f"{filepath}:{line}:{col}: Illegal raw newline inside single-line string literal (started at {start_line}:{start_col})")
                            advance()
                            break
                        if c == '\\':
                            if i + 1 >= n:
                                errors.append(f"{filepath}:{line}:{col}: Trailing backslash at EOF")
                                advance()
                                break
                            esc = text[i + 1]
                            if esc not in VALID_SIMPLE_ESCAPES and esc not in ('u', 'U', 'x'):
                                errors.append(f"{filepath}:{line}:{col}: Invalid escape sequence '\\{esc}' in string literal")
                            advance(2)
                            continue
                        if is_interpolated and c == '{':
                            if i + 1 < n and text[i + 1] == '{':
                                advance(2)
                                continue
                            interp_brace_depth += 1
                            advance()
                            continue
                        if is_interpolated and c == '}' and interp_brace_depth > 0:
                            interp_brace_depth -= 1
                            advance()
                            continue
                        if c == '"' and interp_brace_depth == 0:
                            advance()
                            closed = True
                            break
                        advance()
                    if not closed:
                        errors.append(f"{filepath}:{start_line}:{start_col}: Unterminated single-line string literal")
                    continue

        if ch == "'":
            start_line, start_col = line, col
            advance()
            if i >= n:
                errors.append(f"{filepath}:{start_line}:{start_col}: Unterminated char literal at EOF")
                break
            if text[i] in ('\n', '\r'):
                errors.append(f"{filepath}:{start_line}:{start_col}: Illegal raw newline inside char literal")
                advance()
                continue
            if text[i] == "'":
                errors.append(f"{filepath}:{start_line}:{start_col}: Empty char literal ''")
                advance()
                continue
            if text[i] == '\\':
                advance()
                if i >= n:
                    errors.append(f"{filepath}:{start_line}:{start_col}: Unterminated escape in char literal")
                    break
                esc = text[i]
                if esc in VALID_SIMPLE_ESCAPES:
                    advance()
                elif esc == 'x':
                    advance()
                    hex_digits = 0
                    while i < n and hex_digits < 4 and text[i] in "0123456789abcdefABCDEF":
                        advance()
                        hex_digits += 1
                    if hex_digits == 0:
                        errors.append(f"{filepath}:{start_line}:{start_col}: Invalid \\x escape in char literal")
                elif esc == 'u':
                    advance()
                    for _ in range(4):
                        if i < n and text[i] in "0123456789abcdefABCDEF":
                            advance()
                        else:
                            errors.append(f"{filepath}:{start_line}:{start_col}: Invalid \\u escape in char literal")
                            break
                else:
                    errors.append(f"{filepath}:{start_line}:{start_col}: Unknown escape '\\{esc}' in char literal")
                    advance()
            else:
                advance()

            if i >= n or text[i] != "'":
                errors.append(f"{filepath}:{start_line}:{start_col}: Multi-character or unterminated char literal")
                while i < n and text[i] not in ("'", '\n', '\r'):
                    advance()
                if i < n and text[i] == "'":
                    advance()
            else:
                advance()
            continue

        if ch in ('{', '(', '['):
            brace_stack.append((ch, line, col))
        elif ch in ('}', ')', ']'):
            expected = {'}': '{', ')': '(', ']': '['}[ch]
            if not brace_stack:
                errors.append(f"{filepath}:{line}:{col}: Unmatched closing '{ch}'")
            else:
                top_ch, top_line, top_col = brace_stack.pop()
                if top_ch != expected:
                    errors.append(f"{filepath}:{line}:{col}: Mismatched closing '{ch}' for '{top_ch}' opened at {top_line}:{top_col}")

        advance()

    for open_ch, open_line, open_col in brace_stack:
        errors.append(f"{filepath}:{open_line}:{open_col}: Unclosed '{open_ch}'")

    return errors

def main():
    target_dir = sys.argv[1] if len(sys.argv) > 1 else "."
    all_errors = []
    checked_files = 0
    for root, _, files in os.walk(target_dir):
        for f in sorted(files):
            if f.endswith(".cs"):
                full_path = os.path.join(root, f)
                checked_files += 1
                errs = verify_cs_file(full_path)
                if errs:
                    all_errors.extend(errs)
                else:
                    print(f"  [PASS] C# Lexer & Token Balance Audit: {os.path.relpath(full_path, target_dir)}")

    if checked_files < 10:
        print(f"\n[FAIL] Expected at least 10 C# files, found only {checked_files}!")
        sys.exit(1)

    if all_errors:
        print("\n[FAIL] C# Lexer Errors Detected:")
        for e in all_errors:
            print("  -", e)
        sys.exit(1)

    print(f"\nAll {checked_files} C# files passed strict lexical, escape, and brace-balance verification with 0 warnings/errors.")

if __name__ == "__main__":
    main()
