<%@ Control Language="C#" AutoEventWireup="true" CodeBehind="CustomerCard.ascx.cs" Inherits="Shop.Legacy.Controls.CustomerCard" %>
<asp:Label ID="lblName" runat="server" />
<asp:LoginView ID="lv" runat="server">
    <RoleGroups>
        <asp:RoleGroup Roles="Manager">
            <ContentTemplate>Fido: <%# Eval("CreditLimit") %></ContentTemplate>
        </asp:RoleGroup>
    </RoleGroups>
</asp:LoginView>
