// 轻启 QuickPalette 官网 · 交互脚本
// 设计语言参考腾讯电脑管家官网：bounceUp 入场、胶囊 hover、回到顶部

(function () {
  'use strict';

  // ---------- 入场动画（bounceUp，IntersectionObserver 触发） ----------
  var observer = new IntersectionObserver(function (entries) {
    entries.forEach(function (entry) {
      if (entry.isIntersecting) {
        entry.target.classList.add('reveal-active');
        observer.unobserve(entry.target);
      }
    });
  }, { threshold: 0.12 });

  document.querySelectorAll('.reveal').forEach(function (el) {
    observer.observe(el);
  });

  // ---------- 回到顶部（滚动 > 300px 显示） ----------
  var backTop = document.getElementById('backTop');
  var ticking = false;

  window.addEventListener('scroll', function () {
    if (!ticking) {
      window.requestAnimationFrame(function () {
        if (window.scrollY > 300) {
          backTop.classList.add('show');
        } else {
          backTop.classList.remove('show');
        }
        ticking = false;
      });
      ticking = true;
    }
  });

  backTop.addEventListener('click', function () {
    window.scrollTo({ top: 0, behavior: 'smooth' });
  });

  // ---------- 导航锚点平滑滚动（补偿浮岛高度） ----------
  document.querySelectorAll('a[href^="#"]').forEach(function (link) {
    link.addEventListener('click', function (e) {
      var id = link.getAttribute('href');
      if (!id || id === '#') return;
      var target = document.querySelector(id);
      if (!target) return;
      e.preventDefault();
      var top = target.getBoundingClientRect().top + window.scrollY - 110;
      window.scrollTo({ top: Math.max(0, top), behavior: 'smooth' });
    });
  });
})();
