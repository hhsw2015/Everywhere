import { cli, Strategy } from '@jackwener/opencli/registry';
cli({
  site: 'example',
  name: 'ping',
  description: 'x',
  strategy: Strategy.PUBLIC,
  browser: false,
  args: [],
  columns: ['name', 'value'],
  func: async (args) => {
    return [{ name: '', value: '-' }, { name: '', value: '-' }];
  },
});
