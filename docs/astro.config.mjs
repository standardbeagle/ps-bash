import { defineConfig } from 'astro/config';
import starlight from '@astrojs/starlight';

export default defineConfig({
  site: 'https://dev.standardbeagle.com',
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
          tag: 'script',
          attrs: {
            type: 'module',
            src: 'https://static.cloudflareinsights.com/beacon.min.js',
            'data-cf-beacon': '{"token": "e77d64f1f6f24ed9b18d06d0320e7d1a"}',
          },
        },
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
          attrs: { property: 'og:url', content: 'https://dev.standardbeagle.com/ps-bash/' },
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
            { label: 'ls Provider Architecture', slug: 'reference/ls-provider-architecture' },
          ],
        },
        {
          label: 'Interactive Shell',
          items: [
            { label: 'The Interactive Shell', slug: 'guides/interactive-shell' },
            { label: 'Jump Anywhere: z & zi', slug: 'guides/directory-jumping' },
            { label: 'Styled Output & CSS', slug: 'guides/styled-output' },
            { label: 'Interactive TUIs', slug: 'guides/interactive-tui' },
            { label: 'Media: psav & avtui', slug: 'commands/media' },
          ],
        },
        {
          label: 'Guides',
          items: [
            { label: 'Pipeline Cookbook', slug: 'guides/cookbook' },
            { label: 'Build a CLI Wrapper', slug: 'guides/build-a-cli-wrapper' },
            { label: 'Style It & Make It Interactive', slug: 'guides/build-a-styled-media-tui' },
          ],
        },
      ],
    }),
  ],
});
