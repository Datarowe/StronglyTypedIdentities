﻿using System;

// ReSharper disable once CheckNamespace
namespace Architect.Identities
{
	internal sealed partial class AesPublicIdentityConverter
	{
		public string GetPublicString(ulong id) => this.GetStringCore(id, LongAsciiEncoder, 32);
		public string GetPublicString(long id) => this.GetPublicString(id >= 0 ? (ulong)id : throw new ArgumentOutOfRangeException());

		public string GetPublicShortString(ulong id) => this.GetStringCore(id, ShortAsciiEncoder, 22);
		public string GetPublicShortString(long id) => this.GetPublicShortString(id >= 0 ? (ulong)id : throw new ArgumentOutOfRangeException());

		/// <summary>
		/// Implementation that returns a new string.
		/// </summary>
		private string GetStringCore(ulong id, EncodingFunction encoder, int outputLength)
		{
			return String.Create(outputLength, (Self: this, Id: id, Encoder: encoder), (chars, selfAndIdAndEncoder) =>
			{
				// Call the span-based overload, writing to a temporary byte[]
				Span<byte> bytes = stackalloc byte[chars.Length];
				selfAndIdAndEncoder.Self.GetAsciiBytesCore(selfAndIdAndEncoder.Id, bytes, selfAndIdAndEncoder.Encoder, chars.Length);

				// Convert bytes to chars
				for (var i = 0; i < chars.Length; i++) chars[i] = (char)bytes[i];
			});
		}
	}
}