namespace Nao.Documents

open System
open System.IO.Compression

[<RequireQualifiedAccess>]
module internal PackageValidation =
    let validate (limits: PackageReadLimits) (archive: ZipArchive) =
        if archive.Entries.Count > limits.MaximumEntries then
            raise (PackageLimitExceededException "Package contains too many entries.")

        let mutable expandedBytes = 0L

        for entry in archive.Entries do
            if entry.Length > limits.MaximumEntryBytes then
                raise (PackageLimitExceededException "A package entry exceeds the expanded byte limit.")

            if entry.Length > 0L then
                let compressedBytes = max 1L entry.CompressedLength
                let ratio = float entry.Length / float compressedBytes

                if ratio > limits.MaximumCompressionRatio then
                    raise (PackageLimitExceededException "A package entry exceeds the compression-ratio limit.")

            if expandedBytes > limits.MaximumExpandedBytes - entry.Length then
                raise (PackageLimitExceededException "Package exceeds the total expanded byte limit.")

            expandedBytes <- expandedBytes + entry.Length
