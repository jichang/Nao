namespace Nao.Assistant.Tests

open System
open System.IO
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Assistant

/// Unit tests for the unified per-session file storage (`SessionFiles.SessionFileStore`).
///
/// The session's `files` folder is the single source of truth: uploads, tool output and
/// generated files all share it, and the descriptor index is a *reconciled view* over the
/// folder so files written directly by tools still show up (and deleted files drop out).
[<TestClass>]
type SessionFileStoreTests() =

    /// A fresh store on a unique session key, plus the on-disk session folder so the test
    /// can clean up after itself.
    let newStore () =
        let key = "test/" + Guid.NewGuid().ToString("N")
        let store = SessionFiles.forKey key
        let sessionDir = Directory.GetParent(store.FilesDir).FullName
        store, sessionDir

    let cleanup (sessionDir: string) =
        try
            if Directory.Exists sessionDir then Directory.Delete(sessionDir, true)
        with _ -> ()

    [<TestMethod>]
    member _.Saves_file_under_its_real_name() =
        let store, dir = newStore ()
        try
            let dto = store.Save("notes.md", "text/markdown", "upload", "turn-1", Text.Encoding.UTF8.GetBytes "hello")
            Assert.AreEqual("notes.md", dto.Name)
            // Physically stored under the real name (not "<id>.md").
            Assert.IsTrue(File.Exists(Path.Combine(store.FilesDir, "notes.md")), "file should be on disk under its real name")
            let listed = store.List()
            Assert.AreEqual(1, listed.Length)
            Assert.AreEqual("notes.md", listed.[0].Name)
            Assert.AreEqual("upload", listed.[0].Source)
        finally
            cleanup dir

    [<TestMethod>]
    member _.Re_saving_same_name_keeps_stable_id_and_no_duplicate() =
        let store, dir = newStore ()
        try
            let first = store.Save("doc.txt", "text/plain", "upload", "t1", Text.Encoding.UTF8.GetBytes "v1")
            let second = store.Save("doc.txt", "text/plain", "upload", "t2", Text.Encoding.UTF8.GetBytes "v2-longer")
            Assert.AreEqual(first.Id, second.Id, "re-saving the same name must preserve the id")
            Assert.AreEqual(first.CreatedAt, second.CreatedAt, "creation time must be preserved")
            let listed = store.List()
            Assert.AreEqual(1, listed.Length, "no duplicate descriptor for the same name")
            let _, bytes = (store.TryOpen second.Id).Value
            Assert.AreEqual("v2-longer", Text.Encoding.UTF8.GetString bytes)
        finally
            cleanup dir

    [<TestMethod>]
    member _.Reconcile_picks_up_files_written_directly_to_the_folder() =
        let store, dir = newStore ()
        try
            // Simulate a tool writing straight into the working directory (no Save call).
            let toolFile = Path.Combine(store.FilesDir, "generated.html")
            Directory.CreateDirectory(store.FilesDir) |> ignore
            File.WriteAllText(toolFile, "<h1>hi</h1>")
            let listed = store.List()
            let found = listed |> List.tryFind (fun f -> f.Name = "generated.html")
            Assert.IsTrue(found.IsSome, "a file written directly to the folder should be reconciled into the index")
            Assert.AreEqual("generated", found.Value.Source)
            Assert.AreEqual("text/html", found.Value.MediaType, "media type should be guessed from the extension")
            // And it must be openable by its reconciled id.
            let _, bytes = (store.TryOpen found.Value.Id).Value
            Assert.AreEqual("<h1>hi</h1>", Text.Encoding.UTF8.GetString bytes)
        finally
            cleanup dir

    [<TestMethod>]
    member _.Reconcile_drops_descriptors_whose_file_was_deleted() =
        let store, dir = newStore ()
        try
            let dto = store.Save("temp.txt", "text/plain", "upload", "t1", Text.Encoding.UTF8.GetBytes "bye")
            Assert.AreEqual(1, store.List().Length)
            File.Delete(Path.Combine(store.FilesDir, "temp.txt"))
            Assert.AreEqual(0, store.List().Length, "descriptor should drop once its file is gone")
            Assert.IsTrue((store.TryGet dto.Id).IsNone)
        finally
            cleanup dir

    [<TestMethod>]
    member _.Save_blocks_path_traversal_outside_the_session_folder() =
        let store, dir = newStore ()
        try
            let dto = store.Save("../escape.txt", "text/plain", "upload", "t1", Text.Encoding.UTF8.GetBytes "x")
            // The bytes must land inside the session files folder, never in the parent.
            Assert.IsTrue(File.Exists(Path.Combine(store.FilesDir, "escape.txt")), "file should be confined to the files folder")
            let parentEscape = Path.Combine(Directory.GetParent(store.FilesDir).FullName, "escape.txt")
            Assert.IsFalse(File.Exists parentEscape, "traversal must not write outside the files folder")
            Assert.IsFalse(dto.Name.Contains "..", "stored name must not retain traversal segments")
        finally
            cleanup dir

    [<TestMethod>]
    member _.Supports_subdirectories() =
        let store, dir = newStore ()
        try
            let dto = store.Save("sub/inner.md", "text/markdown", "generated", "t1", Text.Encoding.UTF8.GetBytes "deep")
            Assert.AreEqual("sub/inner.md", dto.Name)
            Assert.IsTrue(File.Exists(Path.Combine(store.FilesDir, "sub", "inner.md")))
            let _, bytes = (store.TryOpen dto.Id).Value
            Assert.AreEqual("deep", Text.Encoding.UTF8.GetString bytes)
        finally
            cleanup dir

    [<TestMethod>]
    member _.EnsureUnique_disambiguates_a_colliding_name_instead_of_overwriting() =
        let store, dir = newStore ()
        try
            // Two different uploads that happen to share a name must both survive.
            let first = store.Save("report.pdf", "application/pdf", "upload", "t1", Text.Encoding.UTF8.GetBytes "first", ensureUnique = true)
            let second = store.Save("report.pdf", "application/pdf", "upload", "t2", Text.Encoding.UTF8.GetBytes "second", ensureUnique = true)
            let third = store.Save("report.pdf", "application/pdf", "upload", "t3", Text.Encoding.UTF8.GetBytes "third", ensureUnique = true)
            Assert.AreEqual("report.pdf", first.Name)
            Assert.AreEqual("report (1).pdf", second.Name, "second upload should be disambiguated")
            Assert.AreEqual("report (2).pdf", third.Name, "third upload should keep counting")
            Assert.AreNotEqual(first.Id, second.Id)
            let listed = store.List()
            Assert.AreEqual(3, listed.Length, "all three uploads must be retained")
            // Each descriptor still maps to its own original bytes.
            Assert.AreEqual("first", Text.Encoding.UTF8.GetString(snd (store.TryOpen first.Id).Value))
            Assert.AreEqual("second", Text.Encoding.UTF8.GetString(snd (store.TryOpen second.Id).Value))
            Assert.AreEqual("third", Text.Encoding.UTF8.GetString(snd (store.TryOpen third.Id).Value))
        finally
            cleanup dir

    [<TestMethod>]
    member _.EnsureUnique_disambiguates_against_a_file_written_directly_to_the_folder() =
        let store, dir = newStore ()
        try
            // A tool wrote "data.csv" straight into the folder (no Save / no index entry).
            Directory.CreateDirectory(store.FilesDir) |> ignore
            File.WriteAllText(Path.Combine(store.FilesDir, "data.csv"), "on-disk")
            let saved = store.Save("data.csv", "text/csv", "upload", "t1", Text.Encoding.UTF8.GetBytes "uploaded", ensureUnique = true)
            Assert.AreEqual("data (1).csv", saved.Name)
            // The pre-existing on-disk file must be left untouched.
            Assert.AreEqual("on-disk", File.ReadAllText(Path.Combine(store.FilesDir, "data.csv")))
            Assert.AreEqual("uploaded", Text.Encoding.UTF8.GetString(snd (store.TryOpen saved.Id).Value))
        finally
            cleanup dir

    [<TestMethod>]
    member _.Save_without_ensureUnique_still_overwrites_same_name() =
        let store, dir = newStore ()
        try
            // Default behaviour (tool rewriting a file) must keep overwriting in place.
            let first = store.Save("out.html", "text/html", "generated", "t1", Text.Encoding.UTF8.GetBytes "v1")
            let second = store.Save("out.html", "text/html", "generated", "t2", Text.Encoding.UTF8.GetBytes "v2")
            Assert.AreEqual("out.html", second.Name)
            Assert.AreEqual(first.Id, second.Id)
            Assert.AreEqual(1, store.List().Length)
        finally
            cleanup dir

