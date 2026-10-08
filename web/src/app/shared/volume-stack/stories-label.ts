/** "1 story", "3 stories": the count of a stack of stories collected in one volume (1.37.0, tankoubon stacks). */
export function storiesLabel(count: number): string {
  return `${count} ${count === 1 ? 'story' : 'stories'}`;
}
