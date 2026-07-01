import { cli, Strategy } from '@jackwener/opencli/registry';
import { CommandExecutionError } from '@jackwener/opencli/errors';
cli({
  site: 'example',
  name: 'browsy',
  description: 'x',
  strategy: Strategy.COOKIE,
  browser: true,
  args: [],
  columns: ['id'],
  func: async (args) => {
    throw new CommandExecutionError('missing page');
  },
});
