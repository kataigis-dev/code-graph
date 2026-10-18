<%@ Page Title="Cliente" Language="C#" MasterPageFile="~/Site.Master" AutoEventWireup="true" CodeBehind="Edit.aspx.cs" Inherits="Shop.Legacy.Customers.Edit" %>
<%@ Register TagPrefix="uc" TagName="CustomerCard" Src="~/Controls/CustomerCard.ascx" %>
<asp:Content ID="Body" ContentPlaceHolderID="MainContent" runat="server">
    <uc:CustomerCard ID="card" runat="server" />
    <asp:TextBox ID="txtName" runat="server" />
    <asp:RequiredFieldValidator ID="valName" runat="server" ControlToValidate="txtName" ErrorMessage="Il nome e' obbligatorio" />
    <asp:TextBox ID="txtCredit" runat="server" />
    <asp:RangeValidator ID="valCredit" runat="server" ControlToValidate="txtCredit" MinimumValue="0" MaximumValue="50000" Type="Currency" ErrorMessage="Fido tra 0 e 50.000" />
    <asp:DropDownList ID="ddlCustomers" runat="server" DataSourceID="dsCustomers" DataTextField="Name" DataValueField="Id"
        AutoPostBack="true" OnSelectedIndexChanged="ddlCustomers_SelectedIndexChanged" />
    <asp:ObjectDataSource ID="dsCustomers" runat="server" TypeName="Shop.Data.OrderRepository" SelectMethod="ListCustomers" />
    <asp:GridView ID="gridOrders" runat="server" DataSourceID="dsOrders" AutoGenerateColumns="false">
        <Columns>
            <asp:BoundField DataField="Total" HeaderText="Totale" />
            <asp:TemplateField><ItemTemplate><%# Eval("Status") %></ItemTemplate></asp:TemplateField>
        </Columns>
    </asp:GridView>
    <asp:SqlDataSource ID="dsOrders" runat="server" ConnectionString="<%$ ConnectionStrings:Shop %>"
        SelectCommand="SELECT Id, Total, Status FROM dbo.Orders WHERE CustomerId = @id"
        UpdateCommand="UPDATE dbo.Orders SET Status = @Status WHERE Id = @Id" />
    <asp:Button ID="btnSave" runat="server" Text="Salva" OnClick="btnSave_Click" />
    <asp:Button ID="btnDeactivate" runat="server" Text="Disattiva" OnClientClick="return confirm('Sicuro?');" />
    <a href="../Admin/Users.aspx">Utenti</a>
</asp:Content>
