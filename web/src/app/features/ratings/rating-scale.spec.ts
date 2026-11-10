import {
  averageStars, isScore, keyScore, ratedLabel, scoreOf, serverAverageLabel, serverAverageText, starIcon, starsText, valueText,
} from './rating-scale';

/** 1.52.0 lane R: the 1-10 score shown as 5 stars with halves (owner decision 5a). */
describe('rating scale', () => {
  it('a score is an integer 1-10', () => {
    expect([1, 5, 10].every(isScore)).toBe(true);
    expect([0, 11, 3.5, -1, NaN, null, undefined, '4'].some(isScore)).toBe(false);
  });

  it('shows score / 2 stars, whole stars without decimals', () => {
    expect([1, 2, 7, 8, 10].map(starsText)).toEqual(['0.5', '1', '3.5', '4', '5']);
    expect(valueText(7)).toBe('3.5 of 5 stars');
    expect(valueText(null)).toBe('Not rated');
    expect(ratedLabel(9)).toBe('Rated 4.5 of 5');
  });

  it('each star is full, half or empty', () => {
    expect([1, 2, 3, 4, 5].map((s) => starIcon(s, 5))).toEqual(['star', 'star', 'star_half', 'star_border', 'star_border']);
    expect([1, 2, 3, 4, 5].map((s) => starIcon(s, null))).toEqual(Array(5).fill('star_border'));
  });

  it('the left half of a star is the half star, the right half the whole star', () => {
    expect(scoreOf(1, 'left')).toBe(1);
    expect(scoreOf(1, 'right')).toBe(2);
    expect(scoreOf(4, 'left')).toBe(7);
    expect(scoreOf(5, 'right')).toBe(10);
  });

  it('the slider keys', () => {
    expect(keyScore('ArrowRight', 4)).toBe(5);
    expect(keyScore('ArrowUp', 10)).toBe(10);
    expect(keyScore('ArrowLeft', 1)).toBe(1);
    expect(keyScore('ArrowDown', 6)).toBe(5);
    expect(keyScore('PageUp', 9)).toBe(10);
    expect(keyScore('PageDown', 2)).toBe(1);
    expect(keyScore('PageUp', null)).toBe(2);
    expect(keyScore('Home', 8)).toBe(1);
    expect(keyScore('End', null)).toBe(10);
    expect(keyScore('Delete', 8)).toBeNull();
    expect(keyScore('Backspace', 8)).toBeNull();
    expect(keyScore('Enter', 8)).toBeUndefined();
    expect(keyScore('ArrowRight', null)).toBe(1);
  });
});

describe('server average text (1.53.0)', () => {
  it('shows the average in stars with one decimal and the count', () => {
    expect(averageStars(8)).toBe('4.0');
    expect(averageStars(7.5)).toBe('3.8');
    expect(averageStars(6.67)).toBe('3.3');
    expect(averageStars(10)).toBe('5.0');
    expect(serverAverageText({ average: 8, count: 3 })).toBe('4.0 · 3 ratings');
    expect(serverAverageText({ average: 2, count: 1 })).toBe('1.0 · 1 rating');
    expect(serverAverageText(null)).toBe('');
    expect(serverAverageText({ average: 8, count: 0 })).toBe('');
    expect(serverAverageLabel({ average: 9, count: 2 })).toBe('Server average 4.5 of 5, 2 ratings');
  });
});
