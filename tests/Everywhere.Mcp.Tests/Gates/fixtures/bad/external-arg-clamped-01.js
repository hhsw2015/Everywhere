import { cli, Strategy } from '@jackwener/opencli/registry';
import { CommandExecutionError } from '@jackwener/opencli/errors';
cli({
  site: 'example',
  name: 'search',
  description: 'x',
  strategy: Strategy.PUBLIC,
  browser: false,
  args: [{ name: 'limit', type: 'number' }],
  columns: ['id'],
  func: async (args) => {
    const capped = Math.min(200, args.limit);
    if (capped === 0) throw new CommandExecutionError('empty');
    return [{ id: capped }];
  },
});
