import { cli, Strategy } from '@jackwener/opencli/registry';
import { ArgumentError, EmptyResultError, CommandExecutionError } from '@jackwener/opencli/errors';
cli({
  site: 'example',
  name: 'search',
  description: 'search x',
  strategy: Strategy.PUBLIC,
  browser: false,
  args: [{ name: 'limit', type: 'number' }],
  columns: ['id', 'title'],
  func: async (args) => {
    if (typeof args.limit !== 'number' || args.limit < 1 || args.limit > 100) {
      throw new ArgumentError('limit must be 1..100');
    }
    const res = await fetch('https://api.example.com/search?limit=' + args.limit);
    if (!res.ok) throw new CommandExecutionError('http_' + res.status);
    const body = await res.json();
    if (!body.items || body.items.length === 0) throw new EmptyResultError('no items');
    return body.items.map((item) => ({ id: item.id, title: item.title }));
  },
});
