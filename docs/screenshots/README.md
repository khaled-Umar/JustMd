# Screenshots

The landing page (`docs/index.html`) currently shows an HTML/CSS illustration of the app window, because no real screenshots exist yet.

To add real ones:

1. Capture the app window on Windows 11 at 100% scaling (Win+Shift+S, window mode), once in Light and once in Dark theme. A document with a table, a task list and a code block shows the most.
2. Save them here as PNG, e.g. `app-light.png` and `app-dark.png`. Keep each under about 300 KB (run them through an optimizer such as `oxipng` or Squoosh).
3. In `docs/index.html`, replace the `<div class="window">…</div>` inside `<figure class="showcase">` with a `<picture>`, and drop the "Illustration, not a screenshot" caption:

   ```html
   <picture>
     <source srcset="screenshots/app-dark.png" media="(prefers-color-scheme: dark)">
     <img src="screenshots/app-light.png" width="1280" height="800"
          alt="JustMd showing a rendered Markdown document">
   </picture>
   ```

4. Optionally reference the same images from the repository `README.md`.
