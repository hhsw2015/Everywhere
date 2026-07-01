import { cli, Strategy } from '@jackwener/opencli/registry';
import { EmptyResultError } from '@jackwener/opencli/errors';
cli({
  site: 'example',
  name: 'browsy',
  description: 'browser adapter',
  strategy: Strategy.COOKIE,
  browser: true,
  args: [],
  columns: ['id'],
  func: async (page, args) => {
    const rows = await page.evaluate(() => Array.from(document.querySelectorAll('.item')).map(el => ({ id: el.id })));
    if (!rows.length) throw new EmptyResultError('no items');
    return rows;
  },
});
