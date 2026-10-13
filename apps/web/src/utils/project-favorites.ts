type FavoriteProject = { projectKey: string; isFavorite: boolean };

export function compareFavoriteProjects(left: FavoriteProject, right: FavoriteProject) {
  return Number(right.isFavorite) - Number(left.isFavorite) || left.projectKey.localeCompare(right.projectKey);
}
