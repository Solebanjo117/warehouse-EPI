using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;

namespace WarehouseEPI.Infrastructure.Production;

internal static class ProductionImportDraftConfiguration
{
    public static void Configure(ModelBuilder builder)
    {
        var draft = builder.Entity<ProductionImportDraft>();
        draft.ToTable("production_import_drafts", t => t.HasCheckConstraint("ck_import_file_size", "octet_length(file_bytes) BETWEEN 1 AND 15728640"));
        draft.HasKey(x => x.Id);
        draft.Property(x => x.Id).HasColumnName("id");
        draft.Property(x => x.OwnerId).HasColumnName("owner_id");
        draft.Property(x => x.FileName).HasColumnName("file_name").HasMaxLength(255);
        draft.Property(x => x.FileHash).HasColumnName("file_hash").HasMaxLength(64);
        draft.Property(x => x.FileBytes).HasColumnName("file_bytes");
        draft.Property(x => x.CreatedAt).HasColumnName("created_at");
        draft.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        draft.Property(x => x.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20);
        draft.Property(x => x.Version).HasColumnName("version").IsConcurrencyToken();
        draft.Property(x => x.BatchId).HasColumnName("batch_id");
        draft.HasIndex(x => new { x.OwnerId, x.UpdatedAt });
        draft.HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Restrict);
        draft.HasOne(x => x.Batch).WithMany().HasForeignKey(x => x.BatchId).OnDelete(DeleteBehavior.Restrict);
        var revision = builder.Entity<ProductionImportRevision>();
        revision.ToTable("production_import_revisions");
        revision.HasKey(x => x.Id);
        revision.Property(x => x.Id).HasColumnName("id");
        revision.Property(x => x.DraftId).HasColumnName("draft_id");
        revision.Property(x => x.Number).HasColumnName("number");
        revision.Property(x => x.ActorId).HasColumnName("actor_id");
        revision.Property(x => x.CreatedAt).HasColumnName("created_at");
        revision.Property(x => x.Action).HasColumnName("action").HasMaxLength(30);
        revision.Property(x => x.ResolutionsJson).HasColumnName("resolutions").HasColumnType("jsonb");
        revision.Property(x => x.PreviewJson).HasColumnName("preview").HasColumnType("jsonb");
        revision.Property(x => x.Fingerprint).HasColumnName("fingerprint").HasMaxLength(64);
        revision.Property(x => x.OperationId).HasColumnName("operation_id");
        revision.HasIndex(x => new { x.DraftId, x.Number }).IsUnique();
        revision.HasIndex(x => x.OperationId).IsUnique();
        revision.HasOne(x => x.Draft).WithMany(x => x.Revisions).HasForeignKey(x => x.DraftId).OnDelete(DeleteBehavior.Restrict);
        revision.HasOne(x => x.Actor).WithMany().HasForeignKey(x => x.ActorId).OnDelete(DeleteBehavior.Restrict);
    }
}
