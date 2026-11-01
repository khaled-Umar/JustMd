Setext heading one
==================

Setext heading two
---

An _emphasis_ paragraph with __underscore strong__, ~~struck~~ and `code`.
Second line of the same paragraph, with trailing spaces for a hard break  
and a backslash break\
and an inline <kbd>Ctrl</kbd> chip, then a line<br>break and<br/>another.

~~~python title="x"
def tilde_fence():
    return "~~~"
~~~

```
backtick fence
```

    indented code
      keeps its indent

|  Name   | Size   |    Note |
|:--------|:------:|--------:|
| `a|b`   |  1     |  x \| y |
| padded  |  22    |         |

- [ ] open task
- [x] done task
- [X] capital done
  with a continuation
* star item

+ plus item

  loose second paragraph

3. three
4) four

A footnote reference[^1] and another[^long].

[^1]: A footnote with several words of text.

[^long]: word

    Continued footnote paragraph.

Term
: Definition one
: Definition two

> [!NOTE]
> An alert with **bold** text.

>lazy quote
without a marker

A [reference link][ref], a [collapsed][] one, a [shortcut] one and an <https://example.com/auto> autolink.

[ref]: https://example.com/ref "Ref title"
[collapsed]: <https://example.com/with space>

[shortcut]:
  https://example.com/shortcut
  'Across lines'
Paragraph directly after a definition.

<div class="note" onclick="alert(1)">
  <b>HTML block</b> with *no* parsing
</div>

<!-- a comment block -->

![Local image](images/photo.png "Title") ![Spaces](<images/my photo.png>) ![Encoded](images/my%20photo%20(1).png)
![Windows path](C:/pics/x.png)

+---+---+
| a | b |
+===+===+
| 1 | 2 |
+---+---+

***

_____

Entities &amp; &copy; and a tab	inside, with trailing spaces   
# Heading closed with hashes #
##No space, so a paragraph
Text ending the file with a definition below.

[last]: https://example.com/last
