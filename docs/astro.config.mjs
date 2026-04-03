import { defineConfig } from 'astro/config';
import starlight from '@astrojs/starlight';

export default defineConfig({
  site: 'https://standardbeagle.github.io',
  base: '/ps-bash',
  integrations: [
    starlight({
      title: 'ps-bash',
      description: 'Real bash commands. Real PowerShell objects. 68 commands bridging Unix and PowerShell.',
      social: [
        { icon: 'github', label: 'GitHub', href: 'https://github.com/standardbeagle/ps-bash' },
      ],
      head: [
        {
          tag: 'meta',
          attrs: { property: 'og:title', content: 'ps-bash' },
        },
        {
          tag: 'meta',
          attrs: { property: 'og:description', content: 'Real bash commands. Real PowerShell objects. 68 commands bridging Unix and PowerShell.' },
        },
        {
          tag: 'meta',
          attrs: { property: 'og:type', content: 'website' },
        },
        {
          tag: 'meta',
          attrs: { property: 'og:url', content: 'https://standardbeagle.github.io/ps-bash/' },
        },
        {
          tag: 'meta',
          attrs: { name: 'twitter:card', content: 'summary' },
        },
      ],
      customCss: ['./src/styles/custom.css'],
      sidebar: [
        {
          label: 'Start Here',
          items: [
            { label: 'Getting Started', slug: 'getting-started' },
            { label: 'Core Concepts', slug: 'core-concepts' },
            { label: 'Comparison', slug: 'comparison' },
          ],
        },
        {
          label: 'Commands',
          autogenerate: { directory: 'commands' },
        },
        {
          label: 'Reference',
          items: [
            { label: 'Object Types', slug: 'reference/object-types' },
            { label: 'Cross-Platform', slug: 'reference/cross-platform' },
          ],
        },
        {
          label: 'Guides',
          items: [
            { label: 'Pipeline Cookbook', slug: 'guides/cookbook' },
          ],
        },
      ],
    }),
  ],
});
