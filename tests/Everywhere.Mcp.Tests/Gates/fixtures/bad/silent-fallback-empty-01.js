import { cli, Strategy } from '@jackwener/opencli/registry';
cli({
  site: 'example',
  name: 'items',
  description: 'x',
  strategy: Strategy.PUBLIC,
  browser: false,
  args: [],
  columns: ['id'],
  func: async (args) => {
    return [];
  },
});
