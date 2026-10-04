import { rerunMessage, rerunReason } from './rerun-labels';

describe('rerunMessage', () => {
  const r = (code: string, nodeId = 'n') => ({ nodeId, code });

  it('says how many were queued when all went through', () => {
    expect(rerunMessage([r('ok'), r('ok')])).toBe('2 items queued to match again');
    expect(rerunMessage([r('ok')])).toBe('1 item queued to match again');
  });

  it('names every refusal reason with its count, in plain words', () => {
    expect(rerunMessage([r('ok'), r('linked'), r('covered_by_folder'), r('covered_by_folder')]))
      .toBe('1 item queued to match again. 3 not queued: 1 already linked - unlink first; '
        + '2 inside a folder that is linked, marked Don\'t match or waiting in review');
  });

  it('says nothing was queued when every item was refused; an unknown code is shown as it is', () => {
    expect(rerunMessage([r('covered_by_folder')])).toBe(
      'Nothing was queued. 1 not queued: 1 inside a folder that is linked, marked Don\'t match or waiting in review');
    expect(rerunReason('budget_exhausted')).toBe('budget_exhausted');
  });
});
