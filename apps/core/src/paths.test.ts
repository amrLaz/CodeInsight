import { test } from 'vitest';
import assert from 'node:assert/strict';

test('paths', async () => {

const { relativeToRoot, repositoryFile } = await import('./context/paths');

assert.equal(repositoryFile('/repo', 'apps/api/Program.cs'), '/repo/apps/api/Program.cs');
assert.equal(repositoryFile('/repo/', 'apps/api/Program.cs'), '/repo/apps/api/Program.cs');
assert.equal(repositoryFile('C:\\src\\repo', 'apps\\api\\Program.cs'), 'C:/src/repo/apps/api/Program.cs');
assert.equal(repositoryFile('/repo', './apps//api/Program.cs'), '/repo/apps/api/Program.cs');

// Report paths arrive over the network: nothing may escape the repository root.
assert.equal(repositoryFile('/repo', '../etc/passwd'), undefined);
assert.equal(repositoryFile('/repo', 'apps/../../etc/passwd'), undefined);
assert.equal(repositoryFile('/repo', '/etc/passwd'), undefined);
assert.equal(repositoryFile('/repo', 'C:/Windows/system.ini'), undefined);
assert.equal(repositoryFile('/repo', ''), undefined);
assert.equal(repositoryFile('', 'apps/api/Program.cs'), undefined);

assert.equal(relativeToRoot('/repo', '/repo/apps/api/Program.cs'), 'apps/api/Program.cs');
assert.equal(relativeToRoot('/repo/', '/repo/apps/api/Program.cs'), 'apps/api/Program.cs');
assert.equal(relativeToRoot('/repo', '/elsewhere/Program.cs'), undefined);
assert.equal(relativeToRoot('/repo', '/repo'), undefined);

console.log('path checks passed');

});
