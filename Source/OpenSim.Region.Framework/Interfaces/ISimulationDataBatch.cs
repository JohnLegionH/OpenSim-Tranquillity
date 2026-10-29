/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

namespace OpenSim.Region.Framework.Interfaces;

/// <summary>
/// Optional. A region data service or store that implements this can save everything one backup pass writes in a
/// single transaction. Scene.Backup calls BeginBackupBatch before it asks the objects to save themselves and
/// EndBackupBatch after, whether or not the pass threw.
/// </summary>
/// <remarks>
/// A store whose writes are cheap to commit one by one (a networked database server) has no need for it and does not
/// implement it. For a file database such as SQLite every commit waits for the disk, so a pass over hundreds of
/// changed objects is far faster as one commit than as hundreds.
///
/// Calls may be nested; the batch ends with the outermost EndBackupBatch. EndBackupBatch without a matching
/// BeginBackupBatch does nothing. Inside a batch each store or remove call still succeeds or fails on its own: a
/// failure undoes that call's writes and leaves the rest of the batch to be committed.
/// </remarks>
public interface ISimulationDataBatch
{
    /// <summary>Starts collecting the writes that follow into one transaction.</summary>
    void BeginBackupBatch();

    /// <summary>Commits the writes collected since the outermost BeginBackupBatch.</summary>
    void EndBackupBatch();
}
