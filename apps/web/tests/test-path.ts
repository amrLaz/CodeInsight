import { pathToFileURL } from 'node:url';

export const sourceUrl = new URL('./src/', pathToFileURL(`${process.cwd()}/`));
