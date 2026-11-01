// Runs synchronously in <head> so the first paint already uses the right theme (no white flash).
(function () {
  'use strict';
  var theme = new URLSearchParams(window.location.search).get('theme');
  document.documentElement.setAttribute('data-theme', theme === 'dark' ? 'dark' : 'light');
})();
