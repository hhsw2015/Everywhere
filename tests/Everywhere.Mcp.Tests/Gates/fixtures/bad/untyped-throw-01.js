import { cli, Strategy } from '@jackwener/opencli/registry';
cli({
  site: 'example',
  name: 'thing',
  description: 'x',
  strategy: Strategy.PUBLIC,
  browser: false,
  args: [],
  columns: ['id'],
  func: async (args) => {
    throw new Error("X");
  },
});
