﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using Azure;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Hosting;

// ReSharper disable once CheckNamespace
namespace Architect.Identities
{
	/// <summary>
	/// <para>
	/// Implementation for application instance ID management through a dedicated Azure Blob Storage Container.
	/// </para>
	/// <para>
	/// This implementation registers the smallest available ID by adding a blob for it.
	/// On application shutdown, it attempts to remove that blob, freeing the ID up again.
	/// </para>
	/// <para>
	/// Enough possible IDs should be available that an occassional failure to free up an ID is not prohibitive.
	/// </para>
	/// </summary>
	internal sealed class AzureBlobApplicationInstanceIdSource : BaseApplicationInstanceIdSource
	{
		public IBlobContainerRepo Repo { get; }

		public AzureBlobApplicationInstanceIdSource(IBlobContainerRepo repo,
			IHostApplicationLifetime applicationLifetime, Action<Exception>? exceptionHandler = null)
			: base(applicationLifetime, exceptionHandler)
		{
			this.Repo = repo ?? throw new ArgumentNullException(nameof(repo));
		}

		protected override ushort GetContextUniqueApplicationInstanceIdCore()
		{
			var metadata = $"ApplicationName={this.GetApplicationName()}\nServerName={this.GetServerName()}\nCreationDateTime={DateTime.UtcNow:O}";
			var metadataBytes = Encoding.UTF8.GetBytes(metadata);
			using var contentStream = new MemoryStream(metadataBytes);

			this.Repo.CreateContainerIfNotExists();

			while (true) // Loop to handle race conditions
			{
				ushort lastBlobId = 0;
				var blobNames = this.Repo.EnumerateBlobNames();
				foreach (var blobName in blobNames)
				{
					if (!UInt16.TryParse(blobName, out var blobId))
						throw new Exception($"{this.GetType().Name} encountered unrelated blobs in the Azure blob storage container.");

					// Break if we have found a gap
					if (blobId > lastBlobId + 1) break;

					lastBlobId = blobId;
				}

				if (lastBlobId == UInt16.MaxValue)
					throw new Exception($"{this.GetType().Name} created an application instance ID overflowing UInt16.");

				var applicationInstanceId = (ushort)(lastBlobId + 1);

				var didCreateBlob = this.Repo.UploadBlob(applicationInstanceId.ToString(), contentStream, overwrite: true);

				if (!didCreateBlob) continue; // Race condition - loop to retry

				return applicationInstanceId;
			}
		}

		protected override void DeleteContextUniqueApplicationInstanceIdCore()
		{
			this.Repo.DeleteBlob(this.ContextUniqueApplicationInstanceId.Value.ToString(), includeSnapshots: true);
		}

		/// <summary>
		/// Represents a client to a particular BlobContainer.
		/// Must be thread-safe.
		/// </summary>
		public interface IBlobContainerRepo
		{
			void CreateContainerIfNotExists();
			IEnumerable<string> EnumerateBlobNames();
			/// <summary>
			/// If overwrite is false and the blob name already exists, this method returns false.
			/// </summary>
			bool UploadBlob(string blobName, Stream contentStream, bool overwrite);
			void DeleteBlob(string blobName, bool includeSnapshots);
		}

		public sealed class BlobContainerRepo : IBlobContainerRepo
		{
			/// <summary>
			/// Thread-safe according to Microsoft: poorly documented, but mentioned on Github and in Microsoft's latest design guidelines.
			/// </summary>
			private BlobContainerClient Client { get; }

			public BlobContainerRepo(BlobContainerClient client)
			{
				this.Client = client ?? throw new ArgumentNullException(nameof(client));
			}

			public void CreateContainerIfNotExists()
			{
				var creationResponse = this.Client.CreateIfNotExists(Azure.Storage.Blobs.Models.PublicAccessType.None);
				var creationResponsStatusCode = creationResponse?.GetRawResponse().Status;
				if (creationResponsStatusCode != null && creationResponsStatusCode != (int)HttpStatusCode.OK && creationResponsStatusCode != (int)HttpStatusCode.Created)
					throw new Exception($"{this.GetType().Name} received an unexpected status code trying to ensure that the container exists: {creationResponsStatusCode}.");
			}

			public IEnumerable<string> EnumerateBlobNames()
			{
				var blobs = this.Client.GetBlobs();
				foreach (var blob in blobs) yield return blob.Name;
			}

			public bool UploadBlob(string blobName, Stream contentStream, bool overwrite)
			{
				var blobClient = this.Client.GetBlobClient(blobName);
				try
				{
					var uploadResponse = blobClient.Upload(contentStream, overwrite: false);
					var uploadResponseStatusCode = uploadResponse.GetRawResponse().Status;
					if (uploadResponseStatusCode != (int)HttpStatusCode.OK && uploadResponseStatusCode != (int)HttpStatusCode.Created)
						throw new Exception($"{this.GetType().Name} received an unexpected status code trying to upload a blob: {uploadResponseStatusCode}.");
					return true;
				}
				catch (RequestFailedException) when (!overwrite)
				{
					// Race condition
					return false;
				}
			}

			public void DeleteBlob(string blobName, bool includeSnapshots)
			{
				var deleteResponse = this.Client.DeleteBlob(blobName, includeSnapshots
					? Azure.Storage.Blobs.Models.DeleteSnapshotsOption.IncludeSnapshots
					: Azure.Storage.Blobs.Models.DeleteSnapshotsOption.None);
				var deleteResponseStatusCode = deleteResponse.Status;
				if (deleteResponseStatusCode != (int)HttpStatusCode.OK && deleteResponseStatusCode != (int)HttpStatusCode.Accepted)
					throw new Exception($"{this.GetType().Name} received an unexpected status code trying to delete a blob: {deleteResponseStatusCode}.");
			}
		}
	}
}
